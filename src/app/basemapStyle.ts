import type maplibregl from 'maplibre-gl';
import { createGameBasemapStyle, createMasterPlanStyle } from './masterPlanStyle';

// Both styles are OpenMapTiles-schema vector basemaps (source-layers water /
// transportation / building), so the analysis overlays + basemap feature
// toggles re-attach identically after a light<->dark swap. Both are keyless.
export const LIGHT_BASEMAP = 'https://tiles.openfreemap.org/styles/liberty';
export const DARK_BASEMAP =
  'https://basemaps.cartocdn.com/gl/dark-matter-gl-style/style.json';

/**
 * The basemap style for the resolved theme. In game (`offline: true`) this is
 * the fully-offline style — paper background only, no streaming vector/aerial
 * tiles — so nothing streams from the network or drapes over the terrain mesh;
 * the local package's aerial + context layers are added on top by
 * `setupAnalysisLayers`. The worldwide picker keeps the streaming master-plan
 * style so live imagery/labels are available while choosing a site.
 */
export function basemapFor(
  _theme: 'light' | 'dark',
  opts?: { offline?: boolean }
): maplibregl.StyleSpecification {
  return opts?.offline ? createGameBasemapStyle() : createMasterPlanStyle();
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
