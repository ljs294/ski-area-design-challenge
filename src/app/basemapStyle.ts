import type maplibregl from 'maplibre-gl';

// Both styles are OpenMapTiles-schema vector basemaps (source-layers water /
// transportation / building), so the analysis overlays + basemap feature
// toggles re-attach identically after a light<->dark swap. Both are keyless.
export const LIGHT_BASEMAP = 'https://tiles.openfreemap.org/styles/liberty';
export const DARK_BASEMAP =
  'https://basemaps.cartocdn.com/gl/dark-matter-gl-style/style.json';

/** The basemap style URL for the resolved theme. */
export function basemapFor(theme: 'light' | 'dark'): string {
  return theme === 'dark' ? DARK_BASEMAP : LIGHT_BASEMAP;
}

/**
 * Light terrain-forward tuning of the basemap: mute the built environment
 * (buildings + urban landuse fills) so hillshade, contours, and the analysis
 * overlays read clearly over natural terrain. A one-time pass on load. No-ops
 * harmlessly for layers/props a given style doesn't have.
 */
export function tuneBasemap(map: maplibregl.Map): void {
  const layers = map.getStyle().layers ?? [];
  for (const l of layers) {
    const sl = (l as { 'source-layer'?: string })['source-layer'];
    try {
      if (sl === 'building') {
        if (l.type === 'fill') map.setPaintProperty(l.id, 'fill-opacity', 0.45);
        else if (l.type === 'fill-extrusion') map.setPaintProperty(l.id, 'fill-extrusion-opacity', 0.4);
      } else if (sl === 'landuse' && l.type === 'fill') {
        map.setPaintProperty(l.id, 'fill-opacity', 0.5);
      }
    } catch {
      // Non-applicable paint prop for this layer — skip.
    }
  }
}
