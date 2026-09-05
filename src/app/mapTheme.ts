import type { Map as MapLibreMap, StyleSpecification } from 'maplibre-gl';

export type MapTheme = 'light' | 'dark';
export interface MapPalette {
  paper: string; text: string; halo: string; contour: string; minorContour: string;
  water: string; waterLine: string; road: string; building: string; selection: string;
}
export const MAP_PALETTES = {
  light: { paper: '#e8e5dc', text: '#263c43', halo: '#f3f5f2', contour: '#faf7eb', minorContour: '#deded0',
    water: '#76a9c4', waterLine: '#397f9f', road: '#625f59', building: '#a7988c', selection: '#155ab6' },
  dark: { paper: '#16252c', text: '#eaf1f4', halo: '#172730', contour: '#c3d3d7', minorContour: '#819aa3',
    water: '#335d72', waterLine: '#86b9cf', road: '#a5b6b8', building: '#74868b', selection: '#80bef2' },
} as const satisfies Record<MapTheme, MapPalette>;

/** Only presentation paint: no geometry, layer order, visibility, or raster changes. */
export function themePaint(id: string, theme: MapTheme): Record<string, unknown> | null {
  const p = MAP_PALETTES[theme];
  if (id === 'dashboard-backdrop') return { 'fill-color': p.paper };
  if (['dashboard-grid', 'dashboard-snow-contours', 'dashboard-trail-ties', 'dashboard-snow-building-outlines'].includes(id)) return { 'line-color': p.text };
  if (id === 'dashboard-snow-water') return { 'fill-color': p.water, 'fill-outline-color': p.waterLine };
  if (id === 'dashboard-snow-buildings') return { 'fill-color': p.building };
  if (id === 'dashboard-trail-nodes') return { 'circle-color': ['case', ['get', 'user'], '#efb84f', ['get', 'terminal'], p.text, p.paper], 'circle-stroke-color': p.text };
  if (id === 'dashboard-guest-label') return { 'text-halo-color': p.halo };
  if (['local-water-selected', 'local-water-line-selected'].includes(id)) return { 'line-color': p.selection };
  if (['dashboard-trail-labels', 'dashboard-snow-building-labels', 'dashboard-snow-flow-arrows', 'dashboard-snow-flow-labels'].includes(id)) return { 'text-color': p.text, 'text-halo-color': p.halo };
  if (id === 'mp-paper') return { 'background-color': p.paper };
  if (id === 'mp-water' || id === 'local-water-fill') return { 'fill-color': p.water };
  if (id === 'mp-waterways' || id === 'local-water-lines') return { 'line-color': p.waterLine };
  if (id === 'mp-roads') return { 'line-color': p.road };
  if (id === 'mp-buildings') return { 'fill-color': p.building };
  if (id === 'contour-lines') return { 'line-color': ['match', ['coalesce', ['get', 'level'], 0], 1, p.contour, p.minorContour] };
  if (['mp-place-labels', 'contour-labels', 'local-water-labels', 'trail-labels', 'lift-labels'].includes(id)) {
    return { 'text-color': p.text, 'text-halo-color': p.halo };
  }
  return null;
}
export function themedBasemap(style: StyleSpecification, theme: MapTheme): StyleSpecification {
  return { ...style, layers: style.layers.map((layer) => {
    const paint = themePaint(layer.id, theme);
    return paint ? { ...layer, paint: { ...layer.paint, ...paint } } as typeof layer : layer;
  }) };
}
export function applyMapTheme(map: MapLibreMap, theme: MapTheme): void {
  for (const layer of map.getStyle()?.layers ?? []) {
    const paint = themePaint(layer.id, theme);
    if (!paint) continue;
    for (const [property, value] of Object.entries(paint)) {
      if (JSON.stringify(map.getPaintProperty(layer.id, property)) !== JSON.stringify(value)) {
        map.setPaintProperty(layer.id, property, value);
      }
    }
  }
}
