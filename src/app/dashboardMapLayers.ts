import maplibregl from 'maplibre-gl';
import { buildingFootprint, isBuildingOwnedPump } from '../buildings';
import type { CoverDisplayGeoJSON } from '../coverDisplay';
import { haversineMeters } from '../geo';
import { toLngLat, toMeters, trailsFromLift, type SkiNetwork } from '../network';
import { snowmakingNodeLabel, snowmakingPipeSegments, snowmakingPipeStats,
  type SnowmakingPipeSegment } from '../snowmakingNetwork';
import type { SnowmakingSegmentAnalysisResult } from '../snowmakingHydraulics';
import type { SavedDam, SavedLift, SavedPond, SavedSnowmakingNode, SavedTrail,
  TerrainRecord } from '../types';
import type { SavedBuilding } from '../types/buildings';
import type { SavedSnowgun, SavedSnowmakingPipe, SnowmakingLakeSource,
  SnowmakingPumpPort } from '../types/snowmaking';
import { DIFFICULTY_COLORS } from '../trails';
import { FILL_BY_CODE } from './coverVectorize';
import type { DashboardKind, SnowmakingDashboardMode } from './dashboardMode';
import { localContourGeoJSON } from './localContours';
import { snowmakingPressureColor } from './snowmakingPressureHeatmap';
import type { Units } from './SettingsContext';
import type { SnowmakingLassoMapState } from './snowmakingLasso';
import type { GuestConnectivity } from './guestConnectivity';

export const DASHBOARD_SOURCE = 'dashboard-map';
export const DASHBOARD_LASSO_SOURCE = 'dashboard-snowmaking-lasso';
export const DASHBOARD_LAYER_IDS = [
  'dashboard-backdrop', 'dashboard-grid', 'dashboard-snow-cover',
  'dashboard-snow-contours', 'dashboard-snow-water', 'dashboard-trail-ties',
  'dashboard-trail-edges', 'dashboard-trail-arrows', 'dashboard-trail-labels',
  'dashboard-trail-nodes', 'dashboard-lift-hit', 'dashboard-trail-hit',
  'dashboard-guest-connection', 'dashboard-guest-halo', 'dashboard-guest-marker', 'dashboard-guest-label',
  'dashboard-snow-buildings', 'dashboard-snow-building-outlines',
  'dashboard-snow-building-labels', 'dashboard-snow-pipes',
  'dashboard-snow-flow-arrows', 'dashboard-snow-flow-labels',
  'dashboard-snow-pump-arrows', 'dashboard-snow-pump-port-labels',
  'dashboard-snow-gun-connections', 'dashboard-snow-nodes',
  'dashboard-snow-hydrants', 'dashboard-snow-node-labels', 'dashboard-snow-gun-lasso-fill',
  'dashboard-snow-gun-lasso-line', 'dashboard-snow-gun-lasso-halo', 'dashboard-snow-guns',
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
  invalidPumpIds: ReadonlySet<string>;
  pressureRange: { minPsi: number; maxPsi: number } | null;
  showGunTypes: boolean;
  operatingGunIds?: ReadonlySet<string>;
  toggleGun(id: string): void;
  setGuns(ids: string[]): void;
  setHoveredSegment(id: string | null): void;
}

export function snowGunFeatureId(id: string): string { return `snow-gun:${id}`; }
export function snowPipeFeatureId(id: string): string { return `snow-pipe:${id}`; }

export type SnowmakingGunVisualState = 'operating-ready' | 'operating-failed' |
  'selected-ready' | 'selected-failed' | 'selected' | 'unselected';

export function snowmakingGunVisualState(input: {
  analysis: boolean;
  selected: boolean;
  status: 'ready' | 'failed' | null | undefined;
  operating: boolean;
}): SnowmakingGunVisualState {
  if (input.analysis && input.operating) {
    return input.status === 'failed' ? 'operating-failed' : 'operating-ready';
  }
  if (input.analysis && input.selected) {
    if (input.status === 'ready') return 'selected-ready';
    if (input.status === 'failed') return 'selected-failed';
    return 'selected';
  }
  return input.analysis ? 'unselected' : input.status === 'ready' ? 'selected-ready'
    : input.status === 'failed' ? 'selected-failed' : 'selected';
}

export function snowmakingGunColor(state: SnowmakingGunVisualState): string {
  return {
    'operating-ready': '#166534', 'operating-failed': '#991b1b',
    'selected-ready': '#86efac', 'selected-failed': '#fca5a5',
    selected: '#000000', unselected: '#9ca3af',
  }[state];
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
  buildings: readonly SavedBuilding[];
  pipes: readonly SavedSnowmakingPipe[];
  guns: readonly SavedSnowgun[];
  coverDisplay: CoverDisplayGeoJSON | null;
  terrainRecord: TerrainRecord | null;
  selectedSnowmaking: { kind: 'node' | 'gun'; id: string } |
    { kind: 'pipe'; id: string; segmentId: string | null } | null;
  snowmakingPresentation: SnowmakingMapPresentation | null;
  snowmakingLasso?: SnowmakingLassoMapState | null;
  guestConnectivity?: GuestConnectivity;
}

type Props = Record<string, string | number | boolean | null>;
type Feature = GeoJSON.Feature<GeoJSON.Geometry, Props>;

function feature(kind: string, geometry: GeoJSON.Geometry, properties: Props = {},
  id?: string): Feature {
  return { type: 'Feature', ...(id ? { id } : {}), properties: { kind, ...properties,
    ...(id ? { featureId: id } : {}) }, geometry };
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

function guestConnectivityFeatures(input: DashboardMapData): Feature[] {
  const status = input.guestConnectivity;
  if (!status?.portal) return [];
  const features = [feature('guest-portal', { type: 'Point', coordinates: [...status.portal.lngLat] }, {
    reachable: status.reachable, label: status.reachable
      ? `Guest Entrance - ${status.connectedLiftName ?? 'connected'}` : 'Resort unreachable',
  }, 'dashboard-guest-portal')];
  if (status.connectionPath.length >= 2) features.unshift(feature('guest-connection', {
    type: 'LineString', coordinates: [...status.connectionPath],
  }, { reachable: status.reachable }, 'dashboard-guest-connection'));
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

export interface SnowmakingDirectionMarker {
  point: [number, number];
  bearing: number;
}

/** MapLibre rotates text from its natural right-facing baseline, while pipe
 * tangents are compass bearings measured clockwise from north. */
export function snowmakingArrowGlyphRotation(bearing: number): number {
  return (bearing + 270) % 360;
}

function bearingBetween(from: [number, number], to: [number, number]): number {
  const phi1 = from[1] * Math.PI / 180, phi2 = to[1] * Math.PI / 180;
  const delta = (to[0] - from[0]) * Math.PI / 180;
  const y = Math.sin(delta) * Math.cos(phi2);
  const x = Math.cos(phi1) * Math.sin(phi2) -
    Math.sin(phi1) * Math.cos(phi2) * Math.cos(delta);
  return (Math.atan2(y, x) * 180 / Math.PI + 360) % 360;
}

export function snowmakingDirectionMarker(
  points: readonly [number, number][],
  fraction = 0.5,
): SnowmakingDirectionMarker | null {
  if (points.length < 2) return null;
  const lengths = points.slice(1).map((point, index) => haversineMeters(points[index], point));
  const total = lengths.reduce((sum, length) => sum + length, 0);
  const target = total * Math.max(0, Math.min(1, fraction));
  let traveled = 0;
  for (let index = 0; index < lengths.length; index += 1) {
    const length = lengths[index];
    if (traveled + length >= target && length > 0) {
      const ratio = (target - traveled) / length;
      return { point: [
        points[index][0] + (points[index + 1][0] - points[index][0]) * ratio,
        points[index][1] + (points[index + 1][1] - points[index][1]) * ratio,
      ], bearing: bearingBetween(points[index], points[index + 1]) };
    }
    traveled += length;
  }
  return null;
}

export function orientedSnowmakingFlow(
  points: readonly [number, number][],
  flowGpm: number,
): { coordinates: [number, number][]; arrow: SnowmakingDirectionMarker | null } {
  const storedMarker = Math.abs(flowGpm) > 0 ? snowmakingDirectionMarker(points) : null;
  const coordinates = flowGpm < 0 ? [...points].reverse() : [...points];
  return { coordinates, arrow: storedMarker && flowGpm < 0
    ? { point: storedMarker.point, bearing: (storedMarker.bearing + 180) % 360 }
    : storedMarker };
}

export function snowmakingPumpArmMarker(
  segment: SnowmakingPipeSegment,
  pumpId: string,
  port: SnowmakingPumpPort,
): SnowmakingDirectionMarker | null {
  const points = segment.vertices.map((vertex) => vertex.point);
  const awayFromPump = segment.fromNodeId === pumpId ? points
    : segment.toNodeId === pumpId ? [...points].reverse() : null;
  if (!awayFromPump) return null;
  const marker = snowmakingDirectionMarker(awayFromPump, 0.18);
  if (!marker || port === 'discharge') return marker;
  return { point: marker.point, bearing: (marker.bearing + 180) % 360 };
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
  const nodeById = new Map(input.nodes.map((node) => [node.id, node]));
  for (const building of input.buildings) {
    if (!isBuildingOwnedPump(building, nodeById.get(building.connection.nodeId))) continue;
    const properties = {
      id: building.id,
      name: building.name,
      pumpNodeId: building.connection.nodeId,
    };
    features.push(feature('snow-building', polygon(buildingFootprint(building)), properties,
      `snow-building:${building.id}`));
    features.push(feature('snow-building-label', {
      type: 'Point', coordinates: building.center,
    }, properties, `snow-building:${building.id}:label`));
  }
  const presentation = input.snowmakingPresentation;
  const solved = new Map((presentation?.segments ?? []).map((segment) => [segment.id, segment]));
  const endpointName = (id: string | null, fallback: string) => {
    const node = id ? nodeById.get(id) : null;
    if (!node) return fallback;
    return node.labelNumber != null
      ? `${node.kind === 'pump' ? 'P' : node.kind === 'hydrant' ? 'H' : 'J'}${node.labelNumber}`
      : snowmakingNodeLabel(node);
  };
  for (const pipe of input.pipes) for (const segment of snowmakingPipeSegments(pipe)) {
    const segmentStats = snowmakingPipeStats(segment.vertices);
    const result = solved.get(segment.id);
    const relevant = presentation?.relevantSegmentColors.get(segment.id) ?? null;
    const pressure = result && presentation?.pressureRange
      ? snowmakingPressureColor((result.upstreamPressurePsi + result.downstreamPressurePsi) / 2,
        presentation.pressureRange) : relevant;
    const rawCoordinates = segment.vertices.map((vertex) => vertex.point);
    const oriented = result ? orientedSnowmakingFlow(rawCoordinates, result.flowGpm)
      : { coordinates: rawCoordinates, arrow: null };
    const forward = !result || result.flowGpm >= 0;
    const flowFrom = endpointName(forward ? segment.fromNodeId : segment.toNodeId,
      `${pipe.name} ${forward ? 'start' : 'end'}`);
    const flowTo = endpointName(forward ? segment.toNodeId : segment.fromNodeId,
      `${pipe.name} ${forward ? 'end' : 'start'}`);
    const flowLabel = result
      ? `${Math.abs(result.flowGpm).toFixed(1)} GPM\n${result.upstreamPressurePsi.toFixed(1)} → ${result.downstreamPressurePsi.toFixed(1)} PSI`
      : '';
    const properties = { id: pipe.id, segmentId: segment.id, segmentIndex: segment.segmentIndex,
      name: pipe.name, diameterIn: pipe.diameterIn, lengthM: segmentStats.lengthM,
      verticalM: segmentStats.verticalM, selected: input.selectedSnowmaking?.kind === 'pipe' &&
        input.selectedSnowmaking.id === pipe.id && (!input.selectedSnowmaking.segmentId ||
          input.selectedSnowmaking.segmentId === segment.id),
      analysis: presentation?.mode === 'analysis',
      relevant: !!relevant, active: result?.active ?? false,
      color: pressure ?? '#2c83a5', flowLabel, flowFrom, flowTo };
    features.push(feature('snow-pipe', {
      type: 'LineString', coordinates: oriented.coordinates,
    }, properties, snowPipeFeatureId(segment.id)));
    if (result?.active && oriented.arrow) features.push(feature('snow-flow-arrow', {
      type: 'Point', coordinates: oriented.arrow.point,
    }, { ...properties, bearing: oriented.arrow.bearing,
      rotation: snowmakingArrowGlyphRotation(oriented.arrow.bearing) },
    `${snowPipeFeatureId(segment.id)}:flow`));
    const midpoint = flowLabel && snowmakingSegmentMidpoint(oriented.coordinates);
    if (midpoint) features.push(feature('snow-pipe-label', {
      type: 'Point', coordinates: midpoint,
    }, properties, `${snowPipeFeatureId(segment.id)}:label`));
  }
  for (const pump of input.nodes.filter((node) => node.kind === 'pump')) {
    for (const pipe of input.pipes) for (const segment of snowmakingPipeSegments(pipe)) {
      const port = segment.fromNodeId === pump.id ? segment.startPumpPort
        : segment.toNodeId === pump.id ? segment.endPumpPort : null;
      if (!port) continue;
      const marker = snowmakingPumpArmMarker(segment, pump.id, port);
      if (!marker) continue;
      features.push(feature('snow-pump-direction', { type: 'Point', coordinates: marker.point }, {
        id: pump.id, segmentId: segment.id, port, portLabel: port === 'suction' ? 'IN' : 'OUT',
        bearing: marker.bearing, rotation: snowmakingArrowGlyphRotation(marker.bearing),
      }, `snow-pump:${pump.id}:${segment.id}`));
    }
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
    selected: input.selectedSnowmaking?.kind === 'node' && input.selectedSnowmaking.id === node.id,
    invalidDirection: presentation?.invalidPumpIds.has(node.id) ?? false }));
  for (const gun of input.guns) {
    features.push(feature('snow-gun', { type: 'Point', coordinates: gun.point }, {
      id: gun.id, connected: !!gun.hydrantId,
      label: presentation?.showGunTypes ? gun.variantId : '',
    }, snowGunFeatureId(gun.id)));
  }
  return features;
}

export function dashboardGeoJSON(input: DashboardMapData): GeoJSON.FeatureCollection {
  const features: Feature[] = [feature('backdrop', WORLD)];
  if (input.kind === 'trails') features.push(...trailFeatures(input));
  if (input.kind === 'snowmaking') features.push(...snowmakingFeatures(input));
  if (input.kind === 'trails' || input.kind === 'snowmaking') features.push(...guestConnectivityFeatures(input));
  return { type: 'FeatureCollection', features };
}

const EMPTY_LASSO: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };

export function dashboardLassoGeoJSON(
  lasso: SnowmakingLassoMapState | null | undefined,
): GeoJSON.FeatureCollection {
  if (!lasso?.ring.length) return EMPTY_LASSO;
  return { type: 'FeatureCollection', features: [{ type: 'Feature', id: 'lasso',
    properties: { kind: 'snow-gun-lasso' }, geometry: {
      type: 'Polygon', coordinates: polygon(lasso.ring).coordinates,
    } }] };
}

function setFeatureState(map: maplibregl.Map, id: string, state: Record<string, unknown>): void {
  map.setFeatureState({ source: DASHBOARD_SOURCE, id }, state);
}

export function applyDashboardGunLassoState(
  map: maplibregl.Map | null,
  nextIds: readonly string[],
  previousIds: readonly string[] = [],
): void {
  if (!map) return;
  const next = new Set(nextIds), previous = new Set(previousIds);
  for (const id of new Set([...previousIds, ...nextIds])) {
    if (previous.has(id) === next.has(id)) continue;
    setFeatureState(map, snowGunFeatureId(id), { lassoed: next.has(id) });
  }
}

export function applyDashboardMapPresentation(
  map: maplibregl.Map | null,
  presentation: SnowmakingMapPresentation | null,
  previous: SnowmakingMapPresentation | null = null,
  gunIds: readonly string[] = [],
): void {
  if (!map || !presentation) return;
  const gunStateIds = new Set([
    ...gunIds, ...presentation.selectedGunIds,
    ...Object.keys(presentation.gunStatuses), ...presentation.operatingGunIds ?? [],
    ...(previous ? [...previous.selectedGunIds, ...Object.keys(previous.gunStatuses),
      ...previous.operatingGunIds ?? []] : []),
  ]);
  for (const id of gunStateIds) {
    const nextState = {
    analysis: presentation.mode === 'analysis',
    selected: presentation.selectedGunIds.has(id),
    status: presentation.gunStatuses[id] ?? null,
    operating: presentation.operatingGunIds?.has(id) ?? false,
    };
    const previousState = previous ? {
      analysis: previous.mode === 'analysis', selected: previous.selectedGunIds.has(id),
      status: previous.gunStatuses[id] ?? null,
      operating: previous.operatingGunIds?.has(id) ?? false,
    } : null;
    if (!previousState || Object.keys(nextState).some((key) =>
      nextState[key as keyof typeof nextState] !== previousState[key as keyof typeof previousState])) {
      setFeatureState(map, snowGunFeatureId(id), nextState);
    }
  }
  const segmentIds = new Set([
    ...(previous?.segments ?? []).map((segment) => segment.id),
    ...presentation.segments.map((segment) => segment.id),
    ...presentation.relevantSegmentColors.keys(),
    ...(previous ? [...previous.relevantSegmentColors.keys()] : []),
  ]);
  for (const id of segmentIds) {
    const nextState = { analysis: presentation.mode === 'analysis',
      active: presentation.segments.some((candidate) => candidate.id === id && candidate.active),
      relevant: presentation.relevantSegmentColors.has(id),
      color: presentation.relevantSegmentColors.get(id) ?? null };
    const previousState = previous && { analysis: previous.mode === 'analysis',
      active: previous.segments.some((candidate) => candidate.id === id && candidate.active),
      relevant: previous.relevantSegmentColors.has(id),
      color: previous.relevantSegmentColors.get(id) ?? null };
    if (!previousState || Object.keys(nextState).some((key) =>
      nextState[key as keyof typeof nextState] !== previousState[key as keyof typeof previousState])) {
      setFeatureState(map, snowPipeFeatureId(id), nextState);
    }
  }
}

const filter = (kind: string): maplibregl.ExpressionSpecification =>
  ['==', ['get', 'kind'], kind] as maplibregl.ExpressionSpecification;
const allFilter = (...rows: maplibregl.ExpressionSpecification[]): maplibregl.FilterSpecification =>
  ['all', ...rows] as maplibregl.FilterSpecification;

export function addDashboardMapLayers(map: maplibregl.Map): void {
  if (map.getSource(DASHBOARD_SOURCE)) return;
  // Feature state uses the stable top-level GeoJSON IDs assigned by the
  // projection. The ordinary `id` property remains the domain ID consumed by
  // the shared hit controller, so promoting it would make pipe segments
  // collide and would address state to the wrong feature.
  map.addSource(DASHBOARD_SOURCE, { type: 'geojson', data: EMPTY, promoteId: 'featureId' });
  map.addSource(DASHBOARD_LASSO_SOURCE, { type: 'geojson', data: EMPTY_LASSO });
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
  const guestColor = ['case', ['get', 'reachable'], '#16a34a', '#dc2626'] as maplibregl.ExpressionSpecification;
  map.addLayer({ id: 'dashboard-guest-connection', type: 'line', source: DASHBOARD_SOURCE,
    filter: filter('guest-connection'), layout: { visibility: 'none' }, paint: {
      'line-color': guestColor, 'line-width': 6, 'line-opacity': 0.85, 'line-dasharray': [2, 1],
    } });
  map.addLayer({ id: 'dashboard-guest-halo', type: 'circle', source: DASHBOARD_SOURCE,
    filter: filter('guest-portal'), layout: { visibility: 'none' }, paint: {
      'circle-color': guestColor, 'circle-radius': 14, 'circle-opacity': 0.22,
    } });
  map.addLayer({ id: 'dashboard-guest-marker', type: 'circle', source: DASHBOARD_SOURCE,
    filter: filter('guest-portal'), layout: { visibility: 'none' }, paint: {
      'circle-color': guestColor, 'circle-radius': 8, 'circle-stroke-color': '#ffffff', 'circle-stroke-width': 2,
    } });
  map.addLayer({ id: 'dashboard-guest-label', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: filter('guest-portal'), layout: { visibility: 'none', 'text-field': ['get', 'label'],
      'text-size': 12, 'text-offset': [0, 1.5], 'text-anchor': 'top', 'text-font': ['Noto Sans Regular'],
      'text-allow-overlap': true }, paint: { 'text-color': guestColor,
      'text-halo-color': '#f4f1ea', 'text-halo-width': 2 } });
  map.addLayer({ id: 'dashboard-lift-hit', type: 'line', source: DASHBOARD_SOURCE,
    filter: allFilter(filter('trail-edge'), ['==', ['get', 'edgeKind'], 'lift']),
    layout: { visibility: 'none' }, paint: { 'line-width': 16, 'line-color': '#000',
      'line-opacity': 0.01 } });
  map.addLayer({ id: 'dashboard-trail-hit', type: 'line', source: DASHBOARD_SOURCE,
    filter: allFilter(filter('trail-edge'), ['!=', ['get', 'edgeKind'], 'lift']),
    layout: { visibility: 'none' }, paint: { 'line-width': 16, 'line-color': '#000',
      'line-opacity': 0.01 } });
  map.addLayer({ id: 'dashboard-snow-buildings', type: 'fill', source: DASHBOARD_SOURCE,
    filter: filter('snow-building'), layout: { visibility: 'none' }, paint: {
      'fill-color': '#a39488', 'fill-opacity': 0.42,
    } });
  map.addLayer({ id: 'dashboard-snow-building-outlines', type: 'line', source: DASHBOARD_SOURCE,
    filter: filter('snow-building'), layout: { visibility: 'none' }, paint: {
      'line-color': '#5f554d', 'line-width': 1.5, 'line-opacity': 0.9,
    } });
  map.addLayer({ id: 'dashboard-snow-building-labels', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: filter('snow-building-label'), layout: { visibility: 'none',
      'text-field': ['get', 'name'], 'text-size': 11, 'text-offset': [0, 2],
      'text-anchor': 'top', 'text-font': ['Noto Sans Regular'], 'text-optional': true,
      'text-allow-overlap': true }, paint: {
      'text-color': '#3f3732', 'text-halo-color': '#f4f1ea', 'text-halo-width': 1.5,
    } });
  map.addLayer({ id: 'dashboard-snow-pipes', type: 'line', source: DASHBOARD_SOURCE,
    filter: filter('snow-pipe'), layout: { visibility: 'none', 'line-cap': 'round',
      'line-join': 'round' }, paint: { 'line-color': ['coalesce', ['feature-state', 'color'], ['get', 'color']],
      'line-width': ['case', ['get', 'selected'], 5, ['interpolate', ['linear'],
        ['get', 'diameterIn'], 4, 2, 24, 4]],
      'line-opacity': ['case', ['all', ['coalesce', ['feature-state', 'analysis'], ['get', 'analysis']],
        ['!', ['coalesce', ['feature-state', 'relevant'], ['get', 'relevant']]]], 0.16,
        ['all', ['coalesce', ['feature-state', 'analysis'], ['get', 'analysis']],
          ['!', ['coalesce', ['feature-state', 'active'], ['get', 'active']]]], 0.45, 1] } });
  map.addLayer({ id: 'dashboard-snow-flow-arrows', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: filter('snow-flow-arrow'), layout: { visibility: 'none',
      // Direction markers must be allowed to turn upside down. MapLibre's
      // default keep-upright behavior is useful for labels, but it mirrors an
      // arrow after a 90-degree turn and makes it point against the solved
      // hydraulic flow.
      'symbol-placement': 'point', 'text-field': '▶', 'text-size': 18,
      'text-font': ['Noto Sans Regular'], 'text-rotate': ['get', 'rotation'],
      'text-rotation-alignment': 'map', 'text-keep-upright': false,
      'text-allow-overlap': true },
    paint: { 'text-color': '#172033', 'text-opacity': 0.95,
      'text-halo-color': '#f4f1ea', 'text-halo-width': 1.25 } });
  map.addLayer({ id: 'dashboard-snow-flow-labels', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: filter('snow-pipe-label'), layout: {
      visibility: 'none', 'symbol-placement': 'point', 'text-field': ['get', 'flowLabel'],
      'text-size': ['interpolate', ['linear'], ['zoom'], 10, 10, 15, 12],
      'text-font': ['Noto Sans Regular'], 'text-line-height': 1.15,
      'text-offset': [0, -1.35], 'text-anchor': 'bottom', 'text-optional': true },
    paint: { 'text-color': '#27303f',
      'text-halo-color': '#f4f1ea', 'text-halo-width': 2 } });
  map.addLayer({ id: 'dashboard-snow-pump-arrows', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: filter('snow-pump-direction'), layout: { visibility: 'none',
      'symbol-placement': 'point', 'text-field': '▶', 'text-size': 16,
      'text-font': ['Noto Sans Regular'], 'text-rotate': ['get', 'rotation'],
      'text-rotation-alignment': 'map', 'text-keep-upright': false,
      'text-allow-overlap': true }, paint: {
      'text-color': ['match', ['get', 'port'], 'suction', '#2563eb', '#d97706'],
      'text-halo-color': '#f4f1ea', 'text-halo-width': 1.5,
    } });
  map.addLayer({ id: 'dashboard-snow-pump-port-labels', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: filter('snow-pump-direction'), layout: { visibility: 'none',
      'symbol-placement': 'point', 'text-field': ['get', 'portLabel'], 'text-size': 9,
      'text-font': ['Noto Sans Regular'], 'text-offset': [0, 1.45],
      'text-allow-overlap': true }, paint: {
      'text-color': ['match', ['get', 'port'], 'suction', '#1d4ed8', '#b45309'],
      'text-halo-color': '#f4f1ea', 'text-halo-width': 1.5,
    } });
  map.addLayer({ id: 'dashboard-snow-gun-connections', type: 'line', source: DASHBOARD_SOURCE,
    filter: filter('snow-gun-connection'), layout: { visibility: 'none' }, paint: {
      'line-color': '#4b5563', 'line-width': 1, 'line-dasharray': [2, 1.5],
    } });
  map.addLayer({ id: 'dashboard-snow-nodes', type: 'circle', source: DASHBOARD_SOURCE,
    filter: allFilter(filter('snow-node'), ['!=', ['get', 'nodeKind'], 'hydrant']),
    layout: { visibility: 'none' }, paint: { 'circle-radius': ['case', ['get', 'selected'], 7, 5],
      'circle-color': ['match', ['get', 'nodeKind'], 'intake', '#397f9f', 'pump', '#f0b44d',
        'junction', '#4b5563', '#397f9f'], 'circle-stroke-color': ['case', ['get', 'selected'],
        '#efb84f', ['case', ['get', 'invalidDirection'], '#dc2626', 'rgba(0,0,0,0)']],
      'circle-stroke-width': ['case', ['any', ['get', 'selected'], ['get', 'invalidDirection']], 2, 0] } });
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
  map.addLayer({ id: 'dashboard-snow-gun-lasso-fill', type: 'fill', source: DASHBOARD_LASSO_SOURCE,
    filter: filter('snow-gun-lasso'), layout: { visibility: 'none' }, paint: {
      'fill-color': '#60a5fa', 'fill-opacity': 0.08,
    } });
  map.addLayer({ id: 'dashboard-snow-gun-lasso-line', type: 'line', source: DASHBOARD_LASSO_SOURCE,
    filter: filter('snow-gun-lasso'), layout: { visibility: 'none' }, paint: {
      'line-color': '#2563eb', 'line-width': 1.5, 'line-dasharray': [2, 2],
      'line-opacity': 0.85,
    } });
  // Draw the preview beneath the gun itself so even an active lasso cannot
  // obscure the authoritative status fill.
  map.addLayer({ id: 'dashboard-snow-gun-lasso-halo', type: 'circle', source: DASHBOARD_SOURCE,
    filter: filter('snow-gun'), layout: { visibility: 'none' }, paint: {
      'circle-radius': 9, 'circle-color': '#60a5fa',
      'circle-opacity': ['case', ['coalesce', ['feature-state', 'lassoed'], false], 0.18, 0],
      'circle-stroke-width': 0,
    } });
  map.addLayer({ id: 'dashboard-snow-guns', type: 'circle', source: DASHBOARD_SOURCE,
    filter: filter('snow-gun'), layout: { visibility: 'none' }, paint: {
      'circle-radius': ['case', ['coalesce', ['feature-state', 'selected'], false], 8, 5],
      'circle-color': ['case',
        ['all', ['feature-state', 'analysis'], ['feature-state', 'operating'],
          ['==', ['feature-state', 'status'], 'failed']], '#991b1b',
        ['all', ['feature-state', 'analysis'], ['feature-state', 'operating']], '#166534',
        ['all', ['feature-state', 'analysis'], ['feature-state', 'selected'],
          ['==', ['feature-state', 'status'], 'failed']], '#fca5a5',
        ['all', ['feature-state', 'analysis'], ['feature-state', 'selected'],
          ['==', ['feature-state', 'status'], 'ready']], '#86efac',
        ['all', ['feature-state', 'analysis'], ['feature-state', 'selected']], '#000000',
        ['feature-state', 'analysis'], '#9ca3af', '#000000'],
      'circle-stroke-color': ['case', ['get', 'connected'], 'rgba(0,0,0,0)', '#dc2626'],
      'circle-stroke-width': ['case', ['get', 'connected'], 0, 1.5],
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
  setDashboardLassoData(map, input.snowmakingLasso ?? null);
  applyDashboardMapPresentation(map, input.snowmakingPresentation,
    null, input.guns.map((gun) => gun.id));
  if (map?.getLayer('dashboard-backdrop')) map.setPaintProperty('dashboard-backdrop',
    'fill-color', input.dark ? '#18202a' : '#f4f1ea');
}

export function setDashboardLassoData(
  map: maplibregl.Map | null,
  lasso: SnowmakingLassoMapState | null,
): void {
  (map?.getSource(DASHBOARD_LASSO_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(dashboardLassoGeoJSON(lasso));
}

export function setDashboardMapVisibility(map: maplibregl.Map, kind: DashboardKind | null): void {
  for (const id of DASHBOARD_LAYER_IDS) if (map.getLayer(id)) {
    const snow = id.startsWith('dashboard-snow-');
    const trail = id.startsWith('dashboard-trail-') || id === 'dashboard-lift-hit';
    const common = id === 'dashboard-backdrop' || id === 'dashboard-grid' || id.startsWith('dashboard-guest-');
    const visible = kind === 'trails' && (common || trail) || kind === 'snowmaking' && (common || snow);
    map.setLayoutProperty(id, 'visibility', visible ? 'visible' : 'none');
  }
}

export function dashboardBounds(input: DashboardMapData): maplibregl.LngLatBoundsLike | null {
  if (input.kind === 'guests') return null;
  const points: [number, number][] = input.kind === 'trails'
    ? input.network.edges.flatMap((edge) => edge.id.endsWith(':r') ? [] : edge.path)
    : [...input.nodes.map((node) => node.point), ...input.pipes.flatMap((pipe) =>
      pipe.vertices.map((vertex) => vertex.point)), ...input.guns.map((gun) => gun.point),
      ...input.ponds.flatMap((pond) => pond.boundary), ...input.dams.flatMap((dam) => dam.pondRings.flat()),
      ...input.lakes.flatMap((lake) => lake.boundary),
      ...input.buildings.flatMap((building) => isBuildingOwnedPump(building,
        input.nodes.find((node) => node.id === building.connection.nodeId))
        ? buildingFootprint(building) : [])];
  if (input.guestConnectivity?.portal) points.push([...input.guestConnectivity.portal.lngLat]);
  for (const point of input.guestConnectivity?.connectionPath ?? []) points.push([...point]);
  if (!points.length) return null;
  return points.reduce((bounds, point) => bounds.extend(point),
    new maplibregl.LngLatBounds(points[0], points[0]));
}
