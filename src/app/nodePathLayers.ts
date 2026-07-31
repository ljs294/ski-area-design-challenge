import type maplibregl from 'maplibre-gl';
import type { SavedNode, SavedPath } from '../skiNodes';

// Node/path rendering: free-standing map pins ("nodes") and the footpaths
// that connect them to lifts, runs, or each other. Mirrors liftLayers.ts —
// one source, per-feature `kind` properties, multiple filtered layers — NOT
// roadLayers.ts's shared-context pattern, because nodes and paths must stay
// independently clickable. Paths are a thinner, quieter connector line than
// lift/trail geometry so they read as "wayfinding" rather than a run;
// complete paths render solid, planning paths dashed (same complete/planning
// split as lifts and trails).

export const NODE_PATH_SOURCE = 'node-paths';
export const NODE_PATH_DRAFT_SOURCE = 'node-path-draft';

// The built (persisted) node/path layers, for a show/hide toggle. Excludes
// the transient path-draft-* layers drawn while placing a node or path.
export const NODE_PATH_BUILT_LAYER_IDS = [
  'path-casing',
  'path-line',
  'path-line-planning',
  'ski-nodes',
  'ski-node-labels',
];

// Neutral slate — distinct from lift red, road taupe, and every trail
// difficulty color so nodes/paths never get mistaken for those.
const NODE_PATH_COLOR = '#4b5563';
const HIGHLIGHT_COLOR = '#f59e0b';

const EMPTY: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };

/** In-progress path: vertices placed so far, cursor tracking the next click,
 *  and any anchor candidates worth highlighting while the user picks a
 *  start/end target (lift terminal, run edge, another path/node). */
export interface NodePathDraft {
  points: [number, number][];
  cursor: [number, number] | null;
  /** Candidate anchor targets to highlight while picking. */
  highlight?: [number, number][];
}

export function nodePathsToGeoJSON(
  nodes: SavedNode[],
  paths: SavedPath[]
): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = [];
  for (const path of paths) {
    features.push({
      type: 'Feature',
      properties: {
        kind: 'path',
        id: path.id,
        name: path.name,
        status: path.status,
        closed: path.closed === true,
      },
      geometry: { type: 'LineString', coordinates: path.points },
    });
  }
  for (const node of nodes) {
    features.push({
      type: 'Feature',
      properties: { kind: 'node', id: node.id, name: node.name },
      geometry: { type: 'Point', coordinates: node.point },
    });
  }
  return { type: 'FeatureCollection', features };
}

/** Adds the node/path source + layers on top of the current style. Idempotent. */
export function addNodePathLayers(map: maplibregl.Map): void {
  if (map.getSource(NODE_PATH_SOURCE)) return;

  map.addSource(NODE_PATH_SOURCE, { type: 'geojson', data: EMPTY });
  // White casing under every path for contrast on any basemap (thinner than
  // the lift casing — paths are a quieter connector, not a headline route).
  map.addLayer({
    id: 'path-casing',
    type: 'line',
    source: NODE_PATH_SOURCE,
    filter: ['==', ['get', 'kind'], 'path'],
    layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#ffffff', 'line-width': 3, 'line-opacity': 0.85 },
  });
  // Complete paths: solid.
  map.addLayer({
    id: 'path-line',
    type: 'line',
    source: NODE_PATH_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'path'], ['==', ['get', 'status'], 'complete']],
    layout: { 'line-cap': 'round' },
    paint: { 'line-color': NODE_PATH_COLOR, 'line-width': 1.5 },
  });
  // Planning paths: dashed.
  map.addLayer({
    id: 'path-line-planning',
    type: 'line',
    source: NODE_PATH_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'path'], ['==', ['get', 'status'], 'planning']],
    paint: { 'line-color': NODE_PATH_COLOR, 'line-width': 1.5, 'line-dasharray': [2, 1.5] },
  });
  // Node pins: styled on lift-terminals (liftLayers.ts) but a neutral/dark
  // fill with a white stroke rather than the lift red, so nodes read as
  // "waypoint" rather than "lift terminal".
  map.addLayer({
    id: 'ski-nodes',
    type: 'circle',
    source: NODE_PATH_SOURCE,
    filter: ['==', ['get', 'kind'], 'node'],
    paint: {
      'circle-radius': 4.5,
      'circle-color': NODE_PATH_COLOR,
      'circle-stroke-color': '#ffffff',
      'circle-stroke-width': 2,
    },
  });
  map.addLayer({
    id: 'ski-node-labels',
    type: 'symbol',
    source: NODE_PATH_SOURCE,
    filter: ['==', ['get', 'kind'], 'node'],
    layout: {
      'text-field': ['get', 'name'],
      'text-size': 12,
      'text-offset': [0, 1.1],
      'text-anchor': 'top',
      // Noto Sans Bold 404s on the dark (Carto) basemap — Regular is the only
      // weight both fontstacks ship, so labels stay visible after a theme swap.
      'text-font': ['Noto Sans Regular'],
      'text-optional': true,
    },
    paint: {
      'text-color': NODE_PATH_COLOR,
      'text-halo-color': '#ffffff',
      'text-halo-width': 1.5,
    },
  });
}

/** Replaces the node/path source data. No-op before the source exists (pre style.load). */
export function setNodePathData(map: maplibregl.Map, nodes: SavedNode[], paths: SavedPath[]): void {
  const src = map.getSource(NODE_PATH_SOURCE) as maplibregl.GeoJSONSource | undefined;
  if (!src) return;
  src.setData(nodePathsToGeoJSON(nodes, paths));
}

/** Pure builder for the draft source data — split out so it's separately testable. */
export function nodePathDraftGeoJSON(draft: NodePathDraft | null): GeoJSON.FeatureCollection {
  if (!draft) return { type: 'FeatureCollection', features: [] };
  const coordinates = [...draft.points];
  if (draft.cursor) coordinates.push(draft.cursor);
  const features: GeoJSON.Feature[] = draft.points.map((point, index) => ({
    type: 'Feature',
    properties: { kind: 'vertex', index },
    geometry: { type: 'Point', coordinates: point },
  }));
  for (const point of draft.highlight ?? []) {
    features.push({
      type: 'Feature',
      properties: { kind: 'highlight' },
      geometry: { type: 'Point', coordinates: point },
    });
  }
  if (coordinates.length >= 2) {
    features.unshift({
      type: 'Feature',
      properties: { kind: 'path-line' },
      geometry: { type: 'LineString', coordinates },
    });
  }
  return { type: 'FeatureCollection', features };
}

/** Adds the transient draft source + layers used while placing a node or
 *  drawing a path. Idempotent. */
export function addNodePathDraftLayers(map: maplibregl.Map): void {
  if (map.getSource(NODE_PATH_DRAFT_SOURCE)) return;
  map.addSource(NODE_PATH_DRAFT_SOURCE, { type: 'geojson', data: nodePathDraftGeoJSON(null) });
  map.addLayer({
    id: 'path-draft-line',
    type: 'line',
    source: NODE_PATH_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'path-line'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': NODE_PATH_COLOR, 'line-width': 2, 'line-dasharray': [2, 1.5] },
  });
  map.addLayer({
    id: 'path-draft-vertices',
    type: 'circle',
    source: NODE_PATH_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'vertex'],
    paint: {
      'circle-radius': 3.5,
      'circle-color': NODE_PATH_COLOR,
      'circle-stroke-color': '#ffffff',
      'circle-stroke-width': 1.5,
    },
  });
  // Candidate anchor targets (lift terminals, run edges, other nodes/paths)
  // worth pointing at while the user is picking one — a hollow amber ring so
  // it never gets confused with the draft's own vertices.
  map.addLayer({
    id: 'path-draft-highlight',
    type: 'circle',
    source: NODE_PATH_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'highlight'],
    paint: {
      'circle-radius': 8,
      'circle-color': HIGHLIGHT_COLOR,
      'circle-opacity': 0.18,
      'circle-stroke-color': HIGHLIGHT_COLOR,
      'circle-stroke-width': 2,
    },
  });
}

/** Replaces the draft source data. No-op before the source exists. */
export function setNodePathDraftData(map: maplibregl.Map, draft: NodePathDraft | null): void {
  (map.getSource(NODE_PATH_DRAFT_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(nodePathDraftGeoJSON(draft));
}
