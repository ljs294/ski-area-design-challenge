import type maplibregl from 'maplibre-gl';

/**
 * Light terrain-forward tuning of the Liberty basemap: mute the built
 * environment (buildings + urban landuse fills) so hillshade, contours, and the
 * analysis overlays read clearly over natural terrain. A one-time pass on load.
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
