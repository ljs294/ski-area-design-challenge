import type maplibregl from 'maplibre-gl';
import type { PlacedGuestPortal } from './guestPortalPlacement';

export const GUEST_SOURCE_ID = 'guest-simulation-points';
export const GUEST_LAYER_ID = 'guest-simulation-dots';
export const GUEST_PORTAL_SOURCE_ID = 'guest-portal';
export const GUEST_PORTAL_LAYER_ID = 'guest-portal-marker';
export const GUEST_LAYER_IDS = [GUEST_LAYER_ID, GUEST_PORTAL_LAYER_ID] as const;

export interface GuestRenderPoint {
  readonly id: string;
  readonly lng: number;
  readonly lat: number;
  readonly status: string;
}

const EMPTY: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };

export function addGuestLayers(map: maplibregl.Map, beforeId?: string): void {
  if (!map.getSource(GUEST_SOURCE_ID)) map.addSource(GUEST_SOURCE_ID, { type: 'geojson', data: EMPTY });
  if (!map.getSource(GUEST_PORTAL_SOURCE_ID)) map.addSource(GUEST_PORTAL_SOURCE_ID, { type: 'geojson', data: EMPTY });
  const before = beforeId && map.getLayer(beforeId) ? beforeId : undefined;
  if (!map.getLayer(GUEST_LAYER_ID)) map.addLayer({ id: GUEST_LAYER_ID, type: 'circle', source: GUEST_SOURCE_ID,
    paint: { 'circle-color': ['match', ['get', 'status'], 'incident', '#dc2626', 'patrol-response', '#dc2626', '#050505'],
      'circle-radius': ['interpolate', ['linear'], ['zoom'], 10, 1.2, 15, 2.5],
      'circle-opacity': 0.9, 'circle-stroke-color': '#ffffff', 'circle-stroke-width': 0.25 } }, before);
  if (!map.getLayer(GUEST_PORTAL_LAYER_ID)) map.addLayer({ id: GUEST_PORTAL_LAYER_ID, type: 'circle', source: GUEST_PORTAL_SOURCE_ID,
    paint: { 'circle-color': '#ef4444', 'circle-radius': 7, 'circle-stroke-color': '#ffffff', 'circle-stroke-width': 2 } }, before);
}

export function setGuestPointData(map: maplibregl.Map | null, points: readonly GuestRenderPoint[]): void {
  const source = map?.getSource(GUEST_SOURCE_ID) as maplibregl.GeoJSONSource | undefined;
  source?.setData({ type: 'FeatureCollection', features: points.map((point) => ({
    type: 'Feature', id: point.id, properties: { id: point.id, status: point.status },
    geometry: { type: 'Point', coordinates: [point.lng, point.lat] },
  })) });
}

export function setGuestPortalData(map: maplibregl.Map | null, portal: PlacedGuestPortal | null): void {
  const source = map?.getSource(GUEST_PORTAL_SOURCE_ID) as maplibregl.GeoJSONSource | undefined;
  source?.setData(portal ? { type: 'FeatureCollection', features: [{ type: 'Feature', id: portal.id,
    properties: { id: portal.id, nodeId: portal.nodeId, label: portal.label },
    geometry: { type: 'Point', coordinates: [...portal.lngLat] } }] } : EMPTY);
}

export function removeGuestLayers(map: maplibregl.Map): void {
  for (const layer of [...GUEST_LAYER_IDS].reverse()) if (map.getLayer(layer)) map.removeLayer(layer);
  if (map.getSource(GUEST_SOURCE_ID)) map.removeSource(GUEST_SOURCE_ID);
  if (map.getSource(GUEST_PORTAL_SOURCE_ID)) map.removeSource(GUEST_PORTAL_SOURCE_ID);
}
