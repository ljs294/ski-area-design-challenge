import type maplibregl from 'maplibre-gl';
import { snowmakingNodeLabel } from '../snowmakingNetwork';
import type { SavedSnowmakingNode, SavedSnowmakingPipe } from '../types/snowmaking';

const SNOWMAKING_SOURCE = 'snowmaking-network';
const SNOWMAKING_DRAFT_SOURCE = 'snowmaking-network-draft';

export const SNOWMAKING_HIT_LAYERS = ['snowmaking-node-hit', 'snowmaking-pipe-hit'] as const;
export const SNOWMAKING_DRAFT_LAYER_IDS = [
  'snowmaking-pipe-draft', 'snowmaking-pipe-draft-vertices', 'snowmaking-snap-preview',
  'snowmaking-hydrant-route', 'snowmaking-hydrant-interval', 'snowmaking-hydrant-endpoints',
  'snowmaking-hydrant-preview', 'snowmaking-hydrant-preview-labels',
] as const;
export const SNOWMAKING_BUILT_LAYER_IDS = [
  'snowmaking-pipe-casing', 'snowmaking-pipes', 'snowmaking-pipe-selected',
  'snowmaking-node-halo', 'snowmaking-nodes', 'snowmaking-node-selected',
  'snowmaking-node-labels', ...SNOWMAKING_HIT_LAYERS,
] as const;

type NetworkProperties = {
  id: string;
  entityKind: 'node' | 'pipe';
  name: string;
  kind?: string;
  label?: string;
  diameterIn?: number;
};

export function snowmakingNetworkToGeoJSON(nodes: readonly SavedSnowmakingNode[],
  pipes: readonly SavedSnowmakingPipe[]): GeoJSON.FeatureCollection<GeoJSON.Geometry, NetworkProperties> {
  return {
    type: 'FeatureCollection',
    features: [
      ...pipes.map((pipe): GeoJSON.Feature<GeoJSON.LineString, NetworkProperties> => ({
        type: 'Feature',
        id: pipe.id,
        properties: { id: pipe.id, entityKind: 'pipe', name: pipe.name,
          diameterIn: pipe.diameterIn },
        geometry: { type: 'LineString', coordinates: pipe.vertices.map((vertex) => vertex.point) },
      })),
      ...nodes.map((node): GeoJSON.Feature<GeoJSON.Point, NetworkProperties> => ({
        type: 'Feature',
        id: node.id,
        properties: { id: node.id, entityKind: 'node', name: node.name,
          kind: node.kind, label: snowmakingNodeLabel(node) },
        geometry: { type: 'Point', coordinates: node.point },
      })),
    ],
  };
}

/** Compatibility projection retained for focused node-layer tests. */
export function snowmakingNodesToGeoJSON(nodes: readonly SavedSnowmakingNode[]):
GeoJSON.FeatureCollection<GeoJSON.Point, NetworkProperties> {
  return { type: 'FeatureCollection', features: nodes.map((node) => ({
    type: 'Feature', id: node.id,
    properties: { id: node.id, entityKind: 'node', name: node.name,
      kind: node.kind, label: snowmakingNodeLabel(node) },
    geometry: { type: 'Point', coordinates: node.point },
  })) };
}

export interface SnowmakingDraftData {
  points: [number, number][];
  cursor: [number, number] | null;
  snapPoint: [number, number] | null;
  selectedRoute?: [number, number][];
  intervalPoints?: [number, number][];
  startPoint?: [number, number] | null;
  endPoint?: [number, number] | null;
  hydrants?: { point: [number, number]; conflict: boolean }[];
}

export function snowmakingDraftToGeoJSON(draft: SnowmakingDraftData | null):
GeoJSON.FeatureCollection<GeoJSON.Geometry, { kind: 'line' | 'vertex' | 'snap' | 'route' |
  'interval' | 'endpoint' | 'hydrant'; label?: string; conflict?: boolean }> {
  if (!draft) return { type: 'FeatureCollection', features: [] };
  const line = draft.cursor ? [...draft.points, draft.cursor] : draft.points;
  const features: GeoJSON.Feature<GeoJSON.Geometry, { kind: 'line' | 'vertex' | 'snap' | 'route' |
    'interval' | 'endpoint' | 'hydrant'; label?: string; conflict?: boolean }>[] = [];
  if (line.length >= 2) features.push({ type: 'Feature', properties: { kind: 'line' },
    geometry: { type: 'LineString', coordinates: line } });
  for (const point of draft.points) features.push({ type: 'Feature', properties: { kind: 'vertex' },
    geometry: { type: 'Point', coordinates: point } });
  if (draft.snapPoint) features.push({ type: 'Feature', properties: { kind: 'snap' },
    geometry: { type: 'Point', coordinates: draft.snapPoint } });
  if ((draft.selectedRoute?.length ?? 0) >= 2) features.push({ type: 'Feature',
    properties: { kind: 'route' }, geometry: { type: 'LineString', coordinates: draft.selectedRoute! } });
  if ((draft.intervalPoints?.length ?? 0) >= 2) features.push({ type: 'Feature',
    properties: { kind: 'interval' }, geometry: { type: 'LineString', coordinates: draft.intervalPoints! } });
  if (draft.startPoint) features.push({ type: 'Feature', properties: { kind: 'endpoint', label: 'S' },
    geometry: { type: 'Point', coordinates: draft.startPoint } });
  if (draft.endPoint) features.push({ type: 'Feature', properties: { kind: 'endpoint', label: 'E' },
    geometry: { type: 'Point', coordinates: draft.endPoint } });
  for (const hydrant of draft.hydrants ?? []) features.push({ type: 'Feature',
    properties: { kind: 'hydrant', conflict: hydrant.conflict,
      label: hydrant.conflict ? '×' : '' }, geometry: { type: 'Point', coordinates: hydrant.point } });
  return { type: 'FeatureCollection', features };
}

export function addSnowmakingLayers(map: maplibregl.Map): void {
  if (map.getSource(SNOWMAKING_SOURCE)) return;
  map.addSource(SNOWMAKING_SOURCE, { type: 'geojson', data: snowmakingNetworkToGeoJSON([], []) });
  map.addSource(SNOWMAKING_DRAFT_SOURCE, { type: 'geojson', data: snowmakingDraftToGeoJSON(null) });

  const pipeFilter: maplibregl.FilterSpecification = ['==', ['get', 'entityKind'], 'pipe'];
  const nodeFilter: maplibregl.FilterSpecification = ['==', ['get', 'entityKind'], 'node'];
  map.addLayer({ id: 'snowmaking-pipe-casing', type: 'line', source: SNOWMAKING_SOURCE,
    filter: pipeFilter, layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#ffffff', 'line-opacity': 0.92,
      'line-width': ['interpolate', ['linear'], ['get', 'diameterIn'], 4, 4, 24, 8] } });
  map.addLayer({ id: 'snowmaking-pipes', type: 'line', source: SNOWMAKING_SOURCE,
    filter: pipeFilter, layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#2c83a5',
      'line-width': ['interpolate', ['linear'], ['get', 'diameterIn'], 4, 2, 24, 6] } });
  map.addLayer({ id: 'snowmaking-pipe-selected', type: 'line', source: SNOWMAKING_SOURCE,
    filter: ['all', pipeFilter, ['==', ['get', 'id'], '']],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#fff1a8', 'line-width': 9, 'line-opacity': 0.7 } });

  map.addLayer({ id: 'snowmaking-node-halo', type: 'circle', source: SNOWMAKING_SOURCE,
    filter: nodeFilter, paint: { 'circle-radius': 9, 'circle-opacity': 0.18,
      'circle-stroke-width': 0, 'circle-color': ['match', ['get', 'kind'],
        'intake', '#397f9f', 'pump', '#f0b44d', 'junction', '#4b5563',
        'hydrant', '#22c55e', '#397f9f'] } });
  map.addLayer({ id: 'snowmaking-nodes', type: 'circle', source: SNOWMAKING_SOURCE,
    filter: nodeFilter, paint: { 'circle-radius': 5.5, 'circle-stroke-color': '#ffffff',
      'circle-stroke-width': 1.5, 'circle-color': ['match', ['get', 'kind'],
        'intake', '#397f9f', 'pump', '#f0b44d', 'junction', '#4b5563',
        'hydrant', '#22c55e', '#397f9f'] } });
  map.addLayer({ id: 'snowmaking-node-selected', type: 'circle', source: SNOWMAKING_SOURCE,
    filter: ['all', nodeFilter, ['==', ['get', 'id'], '']],
    paint: { 'circle-radius': 8, 'circle-color': 'rgba(0,0,0,0)',
      'circle-stroke-color': '#fff6c7', 'circle-stroke-width': 3 } });
  map.addLayer({ id: 'snowmaking-node-labels', type: 'symbol', source: SNOWMAKING_SOURCE,
    filter: nodeFilter, layout: { 'text-field': ['get', 'label'], 'text-size': 12,
      'text-offset': [0, 1.1], 'text-anchor': 'top', 'text-font': ['Noto Sans Regular'],
      'text-optional': true }, paint: { 'text-color': '#1f2937',
      'text-halo-color': '#ffffff', 'text-halo-width': 1.5 } });

  map.addLayer({ id: 'snowmaking-pipe-hit', type: 'line', source: SNOWMAKING_SOURCE,
    filter: pipeFilter, paint: { 'line-width': 16, 'line-color': 'rgba(0,0,0,0)',
      'line-opacity': 0.01 } });
  map.addLayer({ id: 'snowmaking-node-hit', type: 'circle', source: SNOWMAKING_SOURCE,
    filter: nodeFilter, paint: { 'circle-radius': 12, 'circle-color': 'rgba(0,0,0,0)',
      'circle-opacity': 0.01 } });

  map.addLayer({ id: 'snowmaking-pipe-draft', type: 'line', source: SNOWMAKING_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'line'], layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#efb84f', 'line-width': 4, 'line-dasharray': [1.5, 1] } });
  map.addLayer({ id: 'snowmaking-pipe-draft-vertices', type: 'circle',
    source: SNOWMAKING_DRAFT_SOURCE, filter: ['==', ['get', 'kind'], 'vertex'],
    paint: { 'circle-radius': 4.5, 'circle-color': '#efb84f',
      'circle-stroke-color': '#ffffff', 'circle-stroke-width': 1.5 } });
  map.addLayer({ id: 'snowmaking-snap-preview', type: 'circle', source: SNOWMAKING_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'snap'], paint: { 'circle-radius': 9,
      'circle-color': 'rgba(239,184,79,0.18)', 'circle-stroke-color': '#efb84f',
      'circle-stroke-width': 3 } });
  map.addLayer({ id: 'snowmaking-hydrant-route', type: 'line', source: SNOWMAKING_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'route'], layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#fff1a8', 'line-width': 8, 'line-opacity': 0.65 } });
  map.addLayer({ id: 'snowmaking-hydrant-interval', type: 'line', source: SNOWMAKING_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'interval'], layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#efb84f', 'line-width': 5, 'line-opacity': 0.95 } });
  map.addLayer({ id: 'snowmaking-hydrant-endpoints', type: 'symbol', source: SNOWMAKING_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'endpoint'], layout: { 'text-field': ['get', 'label'],
      'text-size': 12, 'text-font': ['Noto Sans Regular'], 'text-allow-overlap': true },
    paint: { 'text-color': '#1f2937', 'text-halo-color': '#fff1a8', 'text-halo-width': 4 } });
  map.addLayer({ id: 'snowmaking-hydrant-preview', type: 'circle', source: SNOWMAKING_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'hydrant'], paint: { 'circle-radius': 6,
      'circle-color': ['case', ['get', 'conflict'], '#ffffff', '#22c55e'],
      'circle-stroke-color': ['case', ['get', 'conflict'], '#b91c1c', '#ffffff'],
      'circle-stroke-width': ['case', ['get', 'conflict'], 3, 1.5] } });
  map.addLayer({ id: 'snowmaking-hydrant-preview-labels', type: 'symbol',
    source: SNOWMAKING_DRAFT_SOURCE, filter: ['all', ['==', ['get', 'kind'], 'hydrant'],
      ['==', ['get', 'conflict'], true]], layout: { 'text-field': ['get', 'label'],
      'text-size': 14, 'text-font': ['Noto Sans Regular'], 'text-allow-overlap': true },
    paint: { 'text-color': '#b91c1c' } });
}

export function setSnowmakingData(map: maplibregl.Map | null,
  nodes: readonly SavedSnowmakingNode[], pipes: readonly SavedSnowmakingPipe[] = []): void {
  (map?.getSource(SNOWMAKING_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(snowmakingNetworkToGeoJSON(nodes, pipes));
}

export function setSelectedSnowmakingNode(map: maplibregl.Map | null, id: string | null): void {
  if (map?.getLayer('snowmaking-node-selected')) {
    map.setFilter('snowmaking-node-selected', ['==', ['get', 'id'], id ?? '']);
  }
  if (map?.getLayer('snowmaking-pipe-selected')) {
    map.setFilter('snowmaking-pipe-selected', ['==', ['get', 'id'], '']);
  }
}

export function setSnowmakingDraftData(map: maplibregl.Map | null,
  draft: SnowmakingDraftData | null): void {
  (map?.getSource(SNOWMAKING_DRAFT_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(snowmakingDraftToGeoJSON(draft));
}

export function setSelectedSnowmakingFeature(map: maplibregl.Map | null,
  selected: { kind: 'node' | 'pipe'; id: string } | null): void {
  if (map?.getLayer('snowmaking-node-selected')) map.setFilter('snowmaking-node-selected',
    ['all', ['==', ['get', 'entityKind'], 'node'],
      ['==', ['get', 'id'], selected?.kind === 'node' ? selected.id : '']]);
  if (map?.getLayer('snowmaking-pipe-selected')) map.setFilter('snowmaking-pipe-selected',
    ['all', ['==', ['get', 'entityKind'], 'pipe'],
      ['==', ['get', 'id'], selected?.kind === 'pipe' ? selected.id : '']]);
}

export function setSnowmakingCaptureTransient(map: maplibregl.Map | null, hidden: boolean): void {
  for (const id of SNOWMAKING_DRAFT_LAYER_IDS) if (map?.getLayer(id)) {
    map.setLayoutProperty(id, 'visibility', hidden ? 'none' : 'visible');
  }
}
