import type maplibregl from 'maplibre-gl';
import type { CoverDisplayGeoJSON } from '../coverDisplay';

export const COVER_SOURCE = 'cover-vector';
export const COVER_FILL_LAYER = 'cover-fill';
export const COVER_OUTLINE_LAYER = 'cover-outline';
export const COVER_LAYER_IDS = [COVER_FILL_LAYER, COVER_OUTLINE_LAYER];

const FILL_BY_CODE: Record<number, string> = {
  1: '#526952', 2: '#d7d8cf', 3: '#b1b791', 4: '#538eae',
  10: '#2f5135', 20: '#71805a', 30: '#c5c899', 40: '#cab88b',
  50: '#9a877d', 60: '#9d978c', 70: '#edf0ee', 80: '#538eae',
  90: '#4f9189', 95: '#276945', 100: '#c0c199',
};

// Water lives in the dedicated OSM/basemap water layer; keep it out of the cover
// fill so lakes and rivers are not painted twice. 4 = four-class water, 80 = ESA
// WorldCover permanent water. Older packages still carry water polygons in their
// persisted geometry, so this render filter — not only display generation — is
// what guarantees they stop double-drawing water.
const NON_WATER_FILTER = [
  'match', ['get', 'code'], [4, 80], false, true,
] as unknown as maplibregl.FilterSpecification;

function colorExpression(fallback: string): maplibregl.ExpressionSpecification {
  return [
    'match', ['get', 'code'],
    ...Object.entries(FILL_BY_CODE).flatMap(([code, color]) => [Number(code), color]),
    fallback,
  ] as unknown as maplibregl.ExpressionSpecification;
}

// Over the aerial base the cover is a translucent classification tint so the
// photo reads through (Stevens Pass Fig 4-2). On the paper fallback there is no
// photo underneath, so the same polygons carry the map at a heavier opacity.
const FILL_OPACITY_OVER_AERIAL = [
  'match', ['get', 'code'],
  1, 0.42, 2, 0.30, 3, 0.34, 4, 0.55,
  10, 0.45, 95, 0.45, 20, 0.42, 80, 0.55, 90, 0.42, 0.36,
] as unknown as maplibregl.ExpressionSpecification;
const FILL_OPACITY_OVER_PAPER = [
  'match', ['get', 'code'],
  1, 0.76, 2, 0.52, 3, 0.48, 4, 0.72,
  10, 0.8, 95, 0.8, 20, 0.74, 80, 0.72, 90, 0.67, 0.55,
] as unknown as maplibregl.ExpressionSpecification;

/** The fill-opacity set for the current base: translucent over the aerial photo,
 *  heavier over the paper fallback so the cover still carries the map on its own. */
export function coverFillOpacity(overAerial: boolean): maplibregl.ExpressionSpecification {
  return overAerial ? FILL_OPACITY_OVER_AERIAL : FILL_OPACITY_OVER_PAPER;
}

/** Re-point the cover fill opacity when the aerial is toggled on/off at runtime. */
export function applyCoverOpacity(map: maplibregl.Map, overAerial: boolean): void {
  if (map.getLayer(COVER_FILL_LAYER)) {
    map.setPaintProperty(COVER_FILL_LAYER, 'fill-opacity', coverFillOpacity(overAerial));
  }
}

/** Add persisted display polygons beneath hillshade. Safe across style reloads. */
export function addCoverLayers(
  map: maplibregl.Map,
  geojson: CoverDisplayGeoJSON,
  visible: boolean,
  before?: string,
  overAerial = false
): void {
  removeCoverLayers(map);
  map.addSource(COVER_SOURCE, { type: 'geojson', data: geojson, maxzoom: 18, tolerance: 0.25 });
  const visibility: 'visible' | 'none' = visible ? 'visible' : 'none';
  map.addLayer({
    id: COVER_FILL_LAYER,
    type: 'fill',
    source: COVER_SOURCE,
    layout: { visibility },
    filter: NON_WATER_FILTER,
    paint: {
      'fill-color': colorExpression('#000000'),
      'fill-opacity': coverFillOpacity(overAerial),
      'fill-antialias': true,
    },
  }, before);
  map.addLayer({
    id: COVER_OUTLINE_LAYER,
    type: 'line',
    source: COVER_SOURCE,
    layout: { visibility, 'line-join': 'round', 'line-cap': 'round' },
    filter: NON_WATER_FILTER,
    paint: {
      'line-color': colorExpression('#4d5c45'),
      'line-width': [
        'interpolate', ['linear'], ['zoom'], 11, 0.25, 16, 1.05,
      ] as unknown as maplibregl.ExpressionSpecification,
      'line-opacity': ['match', ['get', 'code'], 1, 0.72, 2, 0.24, 3, 0.2, 4, 0.52, 10, 0.8, 20, 0.65, 95, 0.8, 0.36] as unknown as maplibregl.ExpressionSpecification,
    },
  }, before);
}

export function removeCoverLayers(map: maplibregl.Map): void {
  for (const id of COVER_LAYER_IDS) if (map.getLayer(id)) map.removeLayer(id);
  if (map.getSource(COVER_SOURCE)) map.removeSource(COVER_SOURCE);
}

/** Push new display polygons to the live cover source (e.g. after a lift clears
 *  a corridor). No-op if the layers have not been added yet. */
export function setCoverData(map: maplibregl.Map, geojson: CoverDisplayGeoJSON): void {
  const src = map.getSource(COVER_SOURCE) as maplibregl.GeoJSONSource | undefined;
  if (src) src.setData(geojson);
}
