import type maplibregl from 'maplibre-gl';
import type { CoverDisplayGeoJSON } from '../coverDisplay';

export const COVER_SOURCE = 'cover-vector';
export const COVER_FILL_LAYER = 'cover-fill';
export const COVER_OUTLINE_LAYER = 'cover-outline';
export const COVER_LAYER_IDS = [COVER_FILL_LAYER, COVER_OUTLINE_LAYER];

const FILL_BY_CODE: Record<number, string> = {
  10: '#2f5135', 20: '#71805a', 30: '#c5c899', 40: '#cab88b',
  50: '#9a877d', 60: '#9d978c', 70: '#edf0ee', 80: '#538eae',
  90: '#4f9189', 95: '#276945', 100: '#c0c199',
};

function colorExpression(fallback: string): maplibregl.ExpressionSpecification {
  return [
    'match', ['get', 'code'],
    ...Object.entries(FILL_BY_CODE).flatMap(([code, color]) => [Number(code), color]),
    fallback,
  ] as unknown as maplibregl.ExpressionSpecification;
}

/** Add persisted display polygons beneath hillshade. Safe across style reloads. */
export function addCoverLayers(
  map: maplibregl.Map,
  geojson: CoverDisplayGeoJSON,
  visible: boolean,
  before?: string
): void {
  removeCoverLayers(map);
  map.addSource(COVER_SOURCE, { type: 'geojson', data: geojson, maxzoom: 18, tolerance: 0.25 });
  const visibility: 'visible' | 'none' = visible ? 'visible' : 'none';
  map.addLayer({
    id: COVER_FILL_LAYER,
    type: 'fill',
    source: COVER_SOURCE,
    layout: { visibility },
    paint: {
      'fill-color': colorExpression('#000000'),
      'fill-opacity': [
        'match', ['get', 'code'],
        10, 0.8, 95, 0.8, 20, 0.74, 80, 0.72, 90, 0.67, 0.55,
      ] as unknown as maplibregl.ExpressionSpecification,
      'fill-antialias': true,
    },
  }, before);
  map.addLayer({
    id: COVER_OUTLINE_LAYER,
    type: 'line',
    source: COVER_SOURCE,
    layout: { visibility, 'line-join': 'round', 'line-cap': 'round' },
    paint: {
      'line-color': colorExpression('#4d5c45'),
      'line-width': [
        'interpolate', ['linear'], ['zoom'], 11, 0.25, 16, 1.05,
      ] as unknown as maplibregl.ExpressionSpecification,
      'line-opacity': ['match', ['get', 'code'], 10, 0.8, 20, 0.65, 95, 0.8, 0.36] as unknown as maplibregl.ExpressionSpecification,
    },
  }, before);
}

export function removeCoverLayers(map: maplibregl.Map): void {
  for (const id of COVER_LAYER_IDS) if (map.getLayer(id)) map.removeLayer(id);
  if (map.getSource(COVER_SOURCE)) map.removeSource(COVER_SOURCE);
}
