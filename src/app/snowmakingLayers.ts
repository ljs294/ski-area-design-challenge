import type maplibregl from 'maplibre-gl';
import type { SavedSnowmakingNode } from '../snowmakingNodes';

// Snowmaking pipe-network nodes: intakes, pumps, junctions, and hydrants.
// Mirrors pondLayers.ts's shape (one source, a small fixed set of layers,
// setXxxData/setSelectedXxx) rather than nodePathLayers.ts's draft-source
// pattern, since there's no in-progress drawing state to render here yet —
// just a saved point set. Per-kind color follows the match-by-`kind`
// approach used for dam/pond preview points and node-path junctions.

const SNOWMAKING_SOURCE = 'snowmaking-network';
export const SNOWMAKING_HIT_LAYERS = ['snowmaking-node-hit'];
export const SNOWMAKING_BUILT_LAYER_IDS = ['snowmaking-node-halo', 'snowmaking-nodes',
  'snowmaking-node-selected', 'snowmaking-node-labels', ...SNOWMAKING_HIT_LAYERS];

export function snowmakingNodesToGeoJSON(nodes: SavedSnowmakingNode[]):
GeoJSON.FeatureCollection<GeoJSON.Point, { id: string; name: string; kind: string }> {
  return { type: 'FeatureCollection', features: nodes.map((node) => ({
    type: 'Feature', properties: { id: node.id, name: node.name, kind: node.kind },
    geometry: { type: 'Point', coordinates: node.point },
  })) };
}

export function addSnowmakingLayers(map: maplibregl.Map): void {
  if (map.getSource(SNOWMAKING_SOURCE)) return;
  map.addSource(SNOWMAKING_SOURCE, { type: 'geojson', data: snowmakingNodesToGeoJSON([]) });
  // Per-kind circle color, staying in the palette family already used for
  // water (pond outline blue), dam/pond preview points (amber), and neutral
  // waypoints (node-path slate). Hydrant gets a fresh saturated green since
  // no existing layer already claims one in this accent family.
  // Soft halo under every kind, same opacity/radius approach as
  // trail-head-candidate (trailLayers.ts) — reads as "network node" at a
  // glance before you're close enough to make out the base dot's color.
  map.addLayer({ id: 'snowmaking-node-halo', type: 'circle', source: SNOWMAKING_SOURCE,
    paint: { 'circle-radius': 9, 'circle-opacity': 0.18, 'circle-stroke-width': 0,
      'circle-color': ['match', ['get', 'kind'], 'intake', '#397f9f', 'pump', '#f0b44d',
        'junction', '#4b5563', 'hydrant', '#22c55e', '#397f9f'] } });
  map.addLayer({ id: 'snowmaking-nodes', type: 'circle', source: SNOWMAKING_SOURCE,
    paint: { 'circle-radius': 5.5, 'circle-stroke-color': '#ffffff', 'circle-stroke-width': 1.5,
      'circle-color': ['match', ['get', 'kind'], 'intake', '#397f9f', 'pump', '#f0b44d',
        'junction', '#4b5563', 'hydrant', '#22c55e', '#397f9f'] } });
  map.addLayer({ id: 'snowmaking-node-selected', type: 'circle', source: SNOWMAKING_SOURCE,
    filter: ['==', ['get', 'id'], ''],
    paint: { 'circle-radius': 8, 'circle-color': 'rgba(0,0,0,0)',
      'circle-stroke-color': '#fff6c7', 'circle-stroke-width': 3 } });
  map.addLayer({ id: 'snowmaking-node-labels', type: 'symbol', source: SNOWMAKING_SOURCE,
    layout: { 'text-field': ['get', 'name'], 'text-size': 12, 'text-offset': [0, 1.1],
      'text-anchor': 'top',
      // Noto Sans Bold 404s on the dark (Carto) basemap — Regular is the
      // only weight both fontstacks ship, so labels stay visible after a
      // theme swap.
      'text-font': ['Noto Sans Regular'], 'text-optional': true },
    paint: { 'text-color': '#1f2937', 'text-halo-color': '#ffffff', 'text-halo-width': 1.5 } });
  map.addLayer({ id: 'snowmaking-node-hit', type: 'circle', source: SNOWMAKING_SOURCE,
    paint: { 'circle-radius': 12, 'circle-color': 'rgba(0,0,0,0)', 'circle-opacity': 0.01 } });
}

export function setSnowmakingData(map: maplibregl.Map | null, nodes: SavedSnowmakingNode[]): void {
  (map?.getSource(SNOWMAKING_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(snowmakingNodesToGeoJSON(nodes));
}

export function setSelectedSnowmakingNode(map: maplibregl.Map | null, id: string | null): void {
  if (map?.getLayer('snowmaking-node-selected'))
    map.setFilter('snowmaking-node-selected', ['==', ['get', 'id'], id ?? '']);
}
