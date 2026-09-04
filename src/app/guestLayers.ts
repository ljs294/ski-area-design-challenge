import type maplibregl from 'maplibre-gl';
import type { PlacedGuestPortal } from './guestPortalPlacement';
import type { GuestConnectivity } from './guestConnectivity';

export const GUEST_SOURCE_ID = 'guest-simulation-points';
export const GUEST_LAYER_ID = 'guest-simulation-dots';
export const GUEST_PORTAL_SOURCE_ID = 'guest-portal';
export const GUEST_PORTAL_CONNECTION_LAYER_ID = 'guest-portal-connection';
export const GUEST_PORTAL_HALO_LAYER_ID = 'guest-portal-halo';
export const GUEST_PORTAL_LAYER_ID = 'guest-portal-marker';
export const GUEST_PORTAL_LABEL_LAYER_ID = 'guest-portal-label';
export const GUEST_LAYER_IDS = [GUEST_PORTAL_CONNECTION_LAYER_ID,
  GUEST_PORTAL_HALO_LAYER_ID, GUEST_PORTAL_LAYER_ID, GUEST_LAYER_ID, GUEST_PORTAL_LABEL_LAYER_ID] as const;

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
  if (!map.getLayer(GUEST_LAYER_ID)) map.addLayer({ id: GUEST_LAYER_ID, type: 'circle', source: GUEST_SOURCE_ID,
    paint: { 'circle-color': ['match', ['get', 'status'],
      'incident', '#dc2626', 'patrol-response', '#dc2626',
      'skiing', '#0ea5e9', 'lift-ride', '#facc15', 'lift-queue', '#f97316',
      'facility-queue', '#a855f7', 'facility-service', '#7e22ce',
      'walking', '#22c55e', 'appraising', '#14b8a6', '#2563eb'],
      'circle-radius': ['interpolate', ['linear'], ['zoom'], 9, 2.75, 13, 4, 16, 6],
      'circle-opacity': 0.95, 'circle-stroke-color': '#ffffff', 'circle-stroke-width': 1 } }, before);
  if (!map.getLayer(GUEST_PORTAL_LABEL_LAYER_ID)) map.addLayer({ id: GUEST_PORTAL_LABEL_LAYER_ID,
    type: 'symbol', source: GUEST_PORTAL_SOURCE_ID, filter: portalFilter, layout: {
      'text-field': ['get', 'statusLabel'], 'text-size': 11, 'text-offset': [0, 1.5],
      'text-anchor': 'top', 'text-font': ['Noto Sans Regular'], 'text-allow-overlap': true },
    paint: { 'text-color': statusColor, 'text-halo-color': '#ffffff', 'text-halo-width': 2 } }, before);
}

export function setGuestPointData(map: maplibregl.Map | null, points: readonly GuestRenderPoint[]): void {
  const source = map?.getSource(GUEST_SOURCE_ID) as maplibregl.GeoJSONSource | undefined;
  source?.setData({ type: 'FeatureCollection', features: points.map(guestPointFeature) });
}

/** Differential animation updates avoid reparsing the entire guest collection every frame. */
export function updateGuestPointData(map: maplibregl.Map | null, previous: readonly GuestRenderPoint[],
  next: readonly GuestRenderPoint[]): void {
  const source = map?.getSource(GUEST_SOURCE_ID) as maplibregl.GeoJSONSource | undefined;
  if (!source) return;
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
}
