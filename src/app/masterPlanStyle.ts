import type { StyleSpecification } from 'maplibre-gl';

export const MASTER_PLAN_LAYER_IDS = {
  satellite: 'mp-satellite', contextLandcover: 'mp-context-landcover', water: 'mp-water',
  waterways: 'mp-waterways', buildings: 'mp-buildings', roads: 'mp-roads', labels: 'mp-place-labels',
} as const;

/** Application-owned subdued aerial + technical context style. */
export function createMasterPlanStyle(): StyleSpecification {
  return {
    version: 8,
    name: 'Mountain Planner Master Plan',
    glyphs: 'https://tiles.openfreemap.org/fonts/{fontstack}/{range}.pbf',
    sources: {
      // Explicit tiles keep style initialization independent of remote
      // TileJSON. Offline context therefore reveals local resort layers over
      // the neutral paper background instead of blocking style.load.
      openmaptiles: { type: 'vector', tiles: ['https://tiles.openfreemap.org/planet/{z}/{x}/{y}.pbf'], maxzoom: 14, attribution: 'OpenFreeMap © OpenMapTiles · Data © OpenStreetMap contributors' },
      satellite: { type: 'raster', tiles: ['https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}'], tileSize: 256, maxzoom: 19, attribution: 'Imagery © Esri, Maxar, Earthstar Geographics' },
    },
    layers: [
      { id: 'mp-paper', type: 'background', paint: { 'background-color': '#e8e5dc' } },
      { id: MASTER_PLAN_LAYER_IDS.satellite, type: 'raster', source: 'satellite', paint: { 'raster-opacity': 0.7, 'raster-saturation': -0.35, 'raster-contrast': -0.08, 'raster-fade-duration': 250 } },
      { id: MASTER_PLAN_LAYER_IDS.contextLandcover, type: 'fill', source: 'openmaptiles', 'source-layer': 'landcover', paint: { 'fill-color': '#65745d', 'fill-opacity': 0.1 } },
      { id: MASTER_PLAN_LAYER_IDS.water, type: 'fill', source: 'openmaptiles', 'source-layer': 'water', paint: { 'fill-color': '#76a9c4', 'fill-opacity': 0.72 } },
      { id: MASTER_PLAN_LAYER_IDS.waterways, type: 'line', source: 'openmaptiles', 'source-layer': 'waterway', paint: { 'line-color': '#438caf', 'line-width': ['interpolate', ['linear'], ['zoom'], 10, 0.5, 16, 1.4], 'line-opacity': 0.8 } },
      { id: MASTER_PLAN_LAYER_IDS.buildings, type: 'fill', source: 'openmaptiles', 'source-layer': 'building', minzoom: 13, paint: { 'fill-color': '#a7988c', 'fill-opacity': 0.42, 'fill-outline-color': '#746a62' } },
      { id: MASTER_PLAN_LAYER_IDS.roads, type: 'line', source: 'openmaptiles', 'source-layer': 'transportation', minzoom: 10, paint: { 'line-color': '#625f59', 'line-opacity': 0.58, 'line-width': ['interpolate', ['linear'], ['zoom'], 10, 0.35, 14, 1, 17, 2.2] } },
      { id: MASTER_PLAN_LAYER_IDS.labels, type: 'symbol', source: 'openmaptiles', 'source-layer': 'place', filter: ['in', ['get', 'class'], ['literal', ['village', 'town', 'city', 'hamlet']]], layout: { 'text-field': ['coalesce', ['get', 'name:latin'], ['get', 'name']], 'text-font': ['Noto Sans Regular'], 'text-size': ['interpolate', ['linear'], ['zoom'], 9, 10, 14, 13] }, paint: { 'text-color': '#343b3b', 'text-halo-color': '#f3f0e8', 'text-halo-width': 1.5 } },
    ],
  } as StyleSpecification;
}
