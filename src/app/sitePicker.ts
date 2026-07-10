import type maplibregl from 'maplibre-gl';

// Live site picker: drag a rectangle, clamp each side to 2–10 km, then lock the
// map view to it (maxBounds). No snapshot/persistence — v1 is live-only.

const M_PER_DEG_LAT = 111320;
export const MIN_KM = 2;
export const MAX_KM = 10;

export interface SiteBox {
  /** [[west, south], [east, north]] */
  bounds: [[number, number], [number, number]];
  widthKm: number;
  heightKm: number;
  areaKm2: number;
}

interface LngLat {
  lng: number;
  lat: number;
}

/**
 * Rectangle from a fixed anchor corner to the dragged corner, with each side
 * clamped to [MIN_KM, MAX_KM]. The anchor stays put; the opposite corner is
 * pulled in/out to satisfy the clamp while preserving drag direction.
 */
export function computeBox(anchor: LngLat, cursor: LngLat): SiteBox {
  const cosLat = Math.cos((anchor.lat * Math.PI) / 180) || 1e-6;
  const rawW = (cursor.lng - anchor.lng) * M_PER_DEG_LAT * cosLat;
  const rawH = (cursor.lat - anchor.lat) * M_PER_DEG_LAT;
  const sx = rawW < 0 ? -1 : 1;
  const sy = rawH < 0 ? -1 : 1;
  const clamp = (m: number) => Math.min(MAX_KM * 1000, Math.max(MIN_KM * 1000, Math.abs(m)));
  const wM = clamp(rawW);
  const hM = clamp(rawH);

  const otherLng = anchor.lng + (sx * wM) / (M_PER_DEG_LAT * cosLat);
  const otherLat = anchor.lat + (sy * hM) / M_PER_DEG_LAT;

  const west = Math.min(anchor.lng, otherLng);
  const east = Math.max(anchor.lng, otherLng);
  const south = Math.min(anchor.lat, otherLat);
  const north = Math.max(anchor.lat, otherLat);

  return {
    bounds: [
      [west, south],
      [east, north],
    ],
    widthKm: wM / 1000,
    heightKm: hM / 1000,
    areaKm2: (wM / 1000) * (hM / 1000),
  };
}

function boxFeature(box: SiteBox): GeoJSON.Feature<GeoJSON.Polygon> {
  const [[w, s], [e, n]] = box.bounds;
  return {
    type: 'Feature',
    properties: {},
    geometry: {
      type: 'Polygon',
      coordinates: [[[w, s], [e, s], [e, n], [w, n], [w, s]]],
    },
  };
}

const EMPTY: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };

export const SITE_SOURCE = 'site-box';
export const MASK_SOURCE = 'site-mask';

/** Context-margin factor: locked pan area = property inflated by this on each side. */
export const MARGIN_FACTOR = 0.4;

/** Outer (pannable) bounds around a property = property inflated by MARGIN_FACTOR. */
export function computeOuterBounds(box: SiteBox): [[number, number], [number, number]] {
  const [[w, s], [e, n]] = box.bounds;
  const dw = (e - w) * MARGIN_FACTOR;
  const dh = (n - s) * MARGIN_FACTOR;
  return [
    [w - dw, s - dh],
    [e + dw, n + dh],
  ];
}

/** World-minus-property polygon (a big outer ring with the property as a hole). */
function maskFeature(box: SiteBox): GeoJSON.Feature<GeoJSON.Polygon> {
  const [[w, s], [e, n]] = box.bounds;
  const world: GeoJSON.Position[] = [
    [-180, -85],
    [180, -85],
    [180, 85],
    [-180, 85],
    [-180, -85],
  ];
  const hole: GeoJSON.Position[] = [[w, s], [e, s], [e, n], [w, n], [w, s]];
  return { type: 'Feature', properties: {}, geometry: { type: 'Polygon', coordinates: [world, hole] } };
}

/**
 * Adds the site sources + layers on top: an exterior dimming mask, the box fill,
 * and two boundary lines (dashed while selecting, solid once locked).
 */
export function addSiteBoxLayers(map: maplibregl.Map): void {
  if (map.getSource(SITE_SOURCE)) return;

  map.addSource(MASK_SOURCE, { type: 'geojson', data: EMPTY });
  map.addLayer({
    id: 'site-mask-fill',
    type: 'fill',
    source: MASK_SOURCE,
    paint: { 'fill-color': '#0a1626', 'fill-opacity': 0.4 },
  });

  map.addSource(SITE_SOURCE, { type: 'geojson', data: EMPTY });
  map.addLayer({
    id: 'site-box-fill',
    type: 'fill',
    source: SITE_SOURCE,
    paint: { 'fill-color': '#2b6cff', 'fill-opacity': 0.1 },
  });
  map.addLayer({
    id: 'site-box-line-dash',
    type: 'line',
    source: SITE_SOURCE,
    paint: { 'line-color': '#2b6cff', 'line-width': 2, 'line-dasharray': [2, 1] },
  });
  map.addLayer({
    id: 'site-box-line-solid',
    type: 'line',
    source: SITE_SOURCE,
    layout: { visibility: 'none' },
    paint: { 'line-color': '#1d4ed8', 'line-width': 3 },
  });
}

/** Updates (or clears, when box is null) the drawn site rectangle. */
export function setSiteBox(map: maplibregl.Map, box: SiteBox | null): void {
  const src = map.getSource(SITE_SOURCE) as maplibregl.GeoJSONSource | undefined;
  if (!src) return;
  src.setData(box ? { type: 'FeatureCollection', features: [boxFeature(box)] } : EMPTY);
}

export type BoundaryMode = 'selecting' | 'locked' | 'off';

/**
 * Switches the boundary presentation:
 *  - selecting: dashed outline, faint interior tint, no exterior mask.
 *  - locked:    solid outline, bright (untinted) interior, exterior dimmed.
 *  - off:       everything cleared.
 */
export function setBoundaryMode(map: maplibregl.Map, mode: BoundaryMode, box?: SiteBox | null): void {
  const dash = mode === 'selecting';
  const locked = mode === 'locked';
  map.setLayoutProperty('site-box-line-dash', 'visibility', dash ? 'visible' : 'none');
  map.setLayoutProperty('site-box-line-solid', 'visibility', locked ? 'visible' : 'none');
  map.setPaintProperty('site-box-fill', 'fill-opacity', locked ? 0 : 0.1);

  const mask = map.getSource(MASK_SOURCE) as maplibregl.GeoJSONSource | undefined;
  mask?.setData(locked && box ? { type: 'FeatureCollection', features: [maskFeature(box)] } : EMPTY);
}
