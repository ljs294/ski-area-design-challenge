import type maplibregl from 'maplibre-gl';
import type { PlacedGuestPortal } from './guestPortalPlacement';
import type { GuestConnectivity } from './guestConnectivity';
import { GuestGpuLayer, type GuestRenderPath, type GuestScreenHit } from './guestGpuLayer';
import type { GuestSimulationRenderFrame } from './guestSimulationWorkerProtocol';

export const GUEST_SOURCE_ID = 'guest-simulation-points';
export const GUEST_LAYER_ID = 'guest-simulation-dots';
export const GUEST_HIT_LAYER_ID = 'guest-simulation-hit';
export const GUEST_PORTAL_SOURCE_ID = 'guest-portal';
export const GUEST_PORTAL_CONNECTION_LAYER_ID = 'guest-portal-connection';
export const GUEST_PORTAL_HALO_LAYER_ID = 'guest-portal-halo';
export const GUEST_PORTAL_LAYER_ID = 'guest-portal-marker';
export const GUEST_PORTAL_LABEL_LAYER_ID = 'guest-portal-label';
export const GUEST_LAYER_IDS = [GUEST_PORTAL_CONNECTION_LAYER_ID,
  GUEST_PORTAL_HALO_LAYER_ID, GUEST_PORTAL_LAYER_ID, GUEST_HIT_LAYER_ID, GUEST_LAYER_ID,
  GUEST_PORTAL_LABEL_LAYER_ID] as const;

const gpuLayers = new WeakMap<maplibregl.Map, GuestGpuLayer>();
const originalQueries = new WeakMap<maplibregl.Map, maplibregl.Map['queryRenderedFeatures']>();
const compactFrames = new WeakMap<maplibregl.Map, {
  readonly frame: GuestSimulationRenderFrame;
  readonly edgePaths: readonly GuestRenderPath[];
  readonly portalLngLat?: readonly [number, number];
}>();

export interface GuestRenderPoint {
  readonly id: string;
  readonly lng: number;
  readonly lat: number;
  readonly status: string;
}

export function interpolateGuestPoints(
  previous: readonly GuestRenderPoint[],
  next: readonly GuestRenderPoint[],
  progress: number,
): readonly GuestRenderPoint[] {
  const fraction = Math.min(1, Math.max(0, progress));
  if (fraction >= 1 || previous.length === 0) return next;
  const previousById = new Map(previous.map((point) => [point.id, point]));
  return next.map((point) => {
    const from = previousById.get(point.id);
    if (!from) return point;
    return { ...point,
      lng: from.lng + (point.lng - from.lng) * fraction,
      lat: from.lat + (point.lat - from.lat) * fraction };
  });
}

const EMPTY: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };

function pointFromQuery(value: unknown): { readonly x: number; readonly y: number } | null {
  if (Array.isArray(value) && value.length === 2
    && typeof value[0] === 'number' && typeof value[1] === 'number') {
    return { x: value[0], y: value[1] };
  }
  if (value && typeof value === 'object' && 'x' in value && 'y' in value
    && typeof value.x === 'number' && typeof value.y === 'number') {
    return { x: value.x, y: value.y };
  }
  return null;
}

function isQueryOptions(value: unknown): value is maplibregl.QueryRenderedFeaturesOptions {
  return !!value && typeof value === 'object' && !Array.isArray(value)
    && !('x' in value) && !('y' in value);
}

function layerList(options: maplibregl.QueryRenderedFeaturesOptions | undefined): readonly string[] | null {
  if (!options?.layers) return null;
  return options.layers instanceof Set ? [...options.layers] : options.layers;
}

function guestHitFeature(map: maplibregl.Map, hit: GuestScreenHit): maplibregl.MapGeoJSONFeature {
  const lngLat = map.unproject([hit.x, hit.y]);
  return {
    type: 'Feature', id: hit.id,
    properties: { id: hit.id, statusFlags: hit.statusFlags },
    geometry: { type: 'Point', coordinates: [lngLat.lng, lngLat.lat] },
    layer: { id: GUEST_HIT_LAYER_ID, type: 'circle', source: GUEST_SOURCE_ID,
      paint: { 'circle-radius': 8, 'circle-opacity': 0 } },
    source: GUEST_SOURCE_ID, state: {},
  } as unknown as maplibregl.MapGeoJSONFeature;
}

/**
 * MapLibre delegated events query style layers through the public
 * queryRenderedFeatures method.  Custom WebGL layers do not participate in
 * that query, so inject the exact screen-space result from the same
 * interpolated buffer used for drawing.  The wrapper is installed once per
 * map and restored when the guest contribution is removed.
 */
function installGuestHitQuery(map: maplibregl.Map): void {
  if (originalQueries.has(map)) return;
  const query = (map as maplibregl.Map & {
    queryRenderedFeatures?: maplibregl.Map['queryRenderedFeatures'];
  }).queryRenderedFeatures;
  if (typeof query !== 'function') return;
  const original = query.bind(map);
  originalQueries.set(map, query);
  const wrapped = ((geometryOrOptions?: unknown, options?: maplibregl.QueryRenderedFeaturesOptions) => {
    const queryOptions = isQueryOptions(geometryOrOptions) ? geometryOrOptions : options;
    const layers = layerList(queryOptions);
    if (!layers?.includes(GUEST_HIT_LAYER_ID) || !map.getLayer(GUEST_HIT_LAYER_ID)
      || map.getLayoutProperty?.(GUEST_HIT_LAYER_ID, 'visibility') === 'none') {
      if (options !== undefined) return original(geometryOrOptions as maplibregl.PointLike, options);
      if (isQueryOptions(geometryOrOptions)) return original(geometryOrOptions);
      return original(geometryOrOptions as maplibregl.PointLike | undefined);
    }
    const point = pointFromQuery(isQueryOptions(geometryOrOptions) ? undefined : geometryOrOptions);
    const otherLayers = layers.filter((id) => id !== GUEST_HIT_LAYER_ID);
    const baseFeatures = otherLayers.length > 0
      ? original(geometryOrOptions as maplibregl.PointLike, { ...queryOptions, layers: otherLayers }) : [];
    const hit = point ? gpuLayers.get(map)?.hitTest(point) : null;
    return hit ? [guestHitFeature(map, hit), ...baseFeatures] : baseFeatures;
  }) as maplibregl.Map['queryRenderedFeatures'];
  (map as maplibregl.Map & { queryRenderedFeatures: maplibregl.Map['queryRenderedFeatures'] }).queryRenderedFeatures = wrapped;
}

function guestPointFeature(point: GuestRenderPoint): GeoJSON.Feature<GeoJSON.Point> {
  return { type: 'Feature', id: point.id, properties: { id: point.id, status: point.status },
    geometry: { type: 'Point', coordinates: [point.lng, point.lat] } };
}

export function addGuestLayers(map: maplibregl.Map, beforeId?: string): void {
  if (!map.getSource(GUEST_SOURCE_ID)) map.addSource(GUEST_SOURCE_ID, { type: 'geojson', data: EMPTY });
  if (!map.getSource(GUEST_PORTAL_SOURCE_ID)) map.addSource(GUEST_PORTAL_SOURCE_ID, { type: 'geojson', data: EMPTY });
  const before = beforeId && map.getLayer(beforeId) ? beforeId : undefined;
  const portalFilter = ['==', ['get', 'kind'], 'portal'] as maplibregl.FilterSpecification;
  const connectionFilter = ['==', ['get', 'kind'], 'connection'] as maplibregl.FilterSpecification;
  const statusColor = ['case', ['get', 'reachable'], '#16a34a', '#dc2626'] as maplibregl.ExpressionSpecification;
  if (!map.getLayer(GUEST_PORTAL_CONNECTION_LAYER_ID)) map.addLayer({ id: GUEST_PORTAL_CONNECTION_LAYER_ID,
    type: 'line', source: GUEST_PORTAL_SOURCE_ID, filter: connectionFilter,
    paint: { 'line-color': statusColor, 'line-width': 5, 'line-opacity': 0.8,
      'line-dasharray': [2, 1] } }, before);
  if (!map.getLayer(GUEST_PORTAL_HALO_LAYER_ID)) map.addLayer({ id: GUEST_PORTAL_HALO_LAYER_ID,
    type: 'circle', source: GUEST_PORTAL_SOURCE_ID, filter: portalFilter,
    paint: { 'circle-color': statusColor, 'circle-radius': 13, 'circle-opacity': 0.22 } }, before);
  if (!map.getLayer(GUEST_PORTAL_LAYER_ID)) map.addLayer({ id: GUEST_PORTAL_LAYER_ID, type: 'circle',
    source: GUEST_PORTAL_SOURCE_ID, filter: portalFilter, paint: { 'circle-color': statusColor,
      'circle-radius': 7, 'circle-stroke-color': '#ffffff', 'circle-stroke-width': 2 } }, before);
  // Arrivals and lift queues often occupy the entrance node. Keep individual
  // guests above the infrastructure marker so a healthy simulation is visible.
  if (!map.getLayer(GUEST_HIT_LAYER_ID)) map.addLayer({ id: GUEST_HIT_LAYER_ID, type: 'circle', source: GUEST_SOURCE_ID,
    paint: { 'circle-radius': 8, 'circle-opacity': 0 } }, before);
  installGuestHitQuery(map);
  if (!map.getLayer(GUEST_LAYER_ID)) {
    const layer = new GuestGpuLayer(GUEST_LAYER_ID);
    gpuLayers.set(map, layer);
    const compact = compactFrames.get(map);
    if (compact) layer.setRenderFrame(compact.frame, compact.edgePaths, compact.portalLngLat, 0);
    map.addLayer(layer, before);
  }
  if (!map.getLayer(GUEST_PORTAL_LABEL_LAYER_ID)) map.addLayer({ id: GUEST_PORTAL_LABEL_LAYER_ID,
    type: 'symbol', source: GUEST_PORTAL_SOURCE_ID, filter: portalFilter, layout: {
      'text-field': ['get', 'statusLabel'], 'text-size': 11, 'text-offset': [0, 1.5],
      'text-anchor': 'top', 'text-font': ['Noto Sans Regular'], 'text-allow-overlap': true },
    paint: { 'text-color': statusColor, 'text-halo-color': '#ffffff', 'text-halo-width': 2 } }, before);
}

export function setGuestPointData(map: maplibregl.Map | null, points: readonly GuestRenderPoint[]): void {
  const layer = map ? gpuLayers.get(map) : undefined;
  if (layer?.hasCompactFrame()) return;
  const source = map?.getSource(GUEST_SOURCE_ID) as maplibregl.GeoJSONSource | undefined;
  source?.setData({ type: 'FeatureCollection', features: points.map(guestPointFeature) });
  if (map) {
    layer?.setPoints([], points, 0);
  }
}

/** Feed the compact worker frame directly to the custom WebGL layer. */
export function setGuestCompactFrame(map: maplibregl.Map | null, frame: GuestSimulationRenderFrame | null,
  edgePaths: readonly GuestRenderPath[], portalLngLat?: readonly [number, number]): void {
  if (!map) return;
  if (!frame) {
    compactFrames.delete(map);
    gpuLayers.get(map)?.setRenderFrame(null, edgePaths, portalLngLat, 0);
    (map.getSource(GUEST_SOURCE_ID) as maplibregl.GeoJSONSource | undefined)?.setData(EMPTY);
    return;
  }
  compactFrames.set(map, { frame, edgePaths, portalLngLat });
  gpuLayers.get(map)?.setRenderFrame(frame, edgePaths, portalLngLat);
}

/** Differential animation updates avoid reparsing the entire guest collection every frame. */
export function updateGuestPointData(map: maplibregl.Map | null, previous: readonly GuestRenderPoint[],
  next: readonly GuestRenderPoint[]): void {
  const source = map?.getSource(GUEST_SOURCE_ID) as maplibregl.GeoJSONSource | undefined;
  if (!source) return;
  const layer = map ? gpuLayers.get(map) : undefined;
  if (layer?.hasCompactFrame()) return;
  layer?.setPoints(previous, next);
  const previousById = new Map(previous.map((point) => [point.id, point]));
  const nextIds = new Set(next.map((point) => point.id));
  const remove = previous.filter((point) => !nextIds.has(point.id)).map((point) => point.id);
  const add = next.filter((point) => !previousById.has(point.id)).map(guestPointFeature);
  const update = next.flatMap((point) => {
    const before = previousById.get(point.id);
    if (!before) return [];
    return [{ id: point.id, newGeometry: { type: 'Point' as const, coordinates: [point.lng, point.lat] },
      ...(before.status === point.status ? {} : { addOrUpdateProperties: [{ key: 'status', value: point.status }] }) }];
  });
  source.updateData({ ...(remove.length ? { remove } : {}), ...(add.length ? { add } : {}),
    ...(update.length ? { update } : {}) });
}

export function setGuestPortalData(map: maplibregl.Map | null, portal: PlacedGuestPortal | null,
  connectivity?: GuestConnectivity): void {
  const source = map?.getSource(GUEST_PORTAL_SOURCE_ID) as maplibregl.GeoJSONSource | undefined;
  if (!portal) { source?.setData(EMPTY); return; }
  const reachable = connectivity?.reachable ?? false;
  const features: GeoJSON.Feature[] = [{ type: 'Feature', id: portal.id,
    properties: { kind: 'portal', id: portal.id, nodeId: portal.nodeId, label: portal.label, reachable,
      statusLabel: reachable ? `Guest Entrance - ${connectivity?.connectedLiftName ?? 'connected'}` : 'Resort unreachable' },
    geometry: { type: 'Point', coordinates: [...portal.lngLat] } }];
  if ((connectivity?.connectionPath.length ?? 0) >= 2) features.unshift({ type: 'Feature',
    id: `${portal.id}:connection`, properties: { kind: 'connection', reachable },
    geometry: { type: 'LineString', coordinates: [...connectivity!.connectionPath] } });
  source?.setData({ type: 'FeatureCollection', features });
}

export function removeGuestLayers(map: maplibregl.Map): void {
  for (const layer of [...GUEST_LAYER_IDS].reverse()) if (map.getLayer(layer)) map.removeLayer(layer);
  if (map.getSource(GUEST_SOURCE_ID)) map.removeSource(GUEST_SOURCE_ID);
  if (map.getSource(GUEST_PORTAL_SOURCE_ID)) map.removeSource(GUEST_PORTAL_SOURCE_ID);
  gpuLayers.delete(map);
  compactFrames.delete(map);
  const original = originalQueries.get(map);
  if (original) {
    (map as maplibregl.Map & { queryRenderedFeatures: maplibregl.Map['queryRenderedFeatures'] }).queryRenderedFeatures = original;
    originalQueries.delete(map);
  }
}
