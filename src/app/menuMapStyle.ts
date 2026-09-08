import type { StyleSpecification } from 'maplibre-gl';

/** Natural menu colors are independent of gameplay presets and UI theme. */
export function createMenuMapStyle(): StyleSpecification {
  return { version: 8, sources: {
    'menu-cover': { type: 'raster', tiles: ['menu-background://cover/{z}/{x}/{y}'], tileSize: 256, maxzoom: 14,
      attribution: '© ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021), processed by ESA WorldCover consortium. Recolored. CC BY 4.0.' },
  }, layers: [
    { id: 'menu-ground', type: 'background', paint: { 'background-color': '#aca99a' } },
    { id: 'menu-cover', type: 'raster', source: 'menu-cover', paint: {
      'raster-opacity': 1, 'raster-fade-duration': 0, 'raster-resampling': 'linear',
    } },
  ] };
}
