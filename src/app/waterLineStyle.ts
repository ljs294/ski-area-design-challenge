import type maplibregl from 'maplibre-gl';

/** Convert real channel width to a legible, zoom-aware map width. */
export function waterLinePixelWidth(latitude: number): maplibregl.ExpressionSpecification {
  const circumferenceM = 40_075_016.686;
  const cosine = Math.max(0.1, Math.cos(latitude * Math.PI / 180));
  const pixelsPerMetre = (zoom: number) => 512 * 2 ** zoom / (circumferenceM * cosine);
  const widthAtZoom = (zoom: number) => [
    'min', 24, ['max', 1, ['*', ['get', 'widthM'], pixelsPerMetre(zoom)]],
  ];
  // MapLibre requires a zoom expression to be the input of the top-level
  // step/interpolate expression. Nesting it inside multiplication causes the
  // water line layer to be rejected when the style is loaded.
  return [
    'interpolate', ['exponential', 2], ['zoom'],
    10, widthAtZoom(10),
    16, widthAtZoom(16),
  ] as unknown as maplibregl.ExpressionSpecification;
}
