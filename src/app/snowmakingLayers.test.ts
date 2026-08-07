import { describe, expect, it } from 'vitest';
import type maplibregl from 'maplibre-gl';
import {
  SNOWMAKING_BUILT_LAYER_IDS,
  SNOWMAKING_DRAFT_LAYER_IDS,
  SNOWMAKING_HIT_LAYERS,
  addSnowmakingLayers,
  setSelectedSnowmakingNode,
  setSnowmakingData,
  snowmakingNodesToGeoJSON,
  snowmakingDraftToGeoJSON,
} from './snowmakingLayers';
import type { SavedSnowmakingNode } from '../types/snowmaking';

const NODES: SavedSnowmakingNode[] = [
  { id: 'node-1', name: 'Upper Pond Intake', kind: 'intake', point: [-121.5, 47.2],
    elevM: 1200, source: { kind: 'pond', pondId: 'pond-1' }, createdAt: 'now' },
  { id: 'node-2', name: 'Pump House 1', kind: 'pump', point: [-121.51, 47.21],
    elevM: 1210, createdAt: 'now' },
  { id: 'node-3', name: 'Hydrant 3', kind: 'hydrant', point: [-121.52, 47.22],
    elevM: 1220, createdAt: 'now' },
];

describe('snowmaking network map layers', () => {
  it('renders saved nodes as points with id/name/kind properties', () => {
    const data = snowmakingNodesToGeoJSON(NODES);
    expect(data.features).toHaveLength(3);
    for (const [i, node] of NODES.entries()) {
      expect(data.features[i].geometry.type).toBe('Point');
      expect(data.features[i].geometry.coordinates).toEqual(node.point);
      expect(data.features[i].properties?.id).toBe(node.id);
      expect(data.features[i].properties?.name).toBe(node.name);
      expect(data.features[i].properties?.kind).toBe(node.kind);
    }
  });

  it('produces an empty FeatureCollection for an empty node list, not an error', () => {
    const data = snowmakingNodesToGeoJSON([]);
    expect(data).toEqual({ type: 'FeatureCollection', features: [] });
  });

  it('keeps the hit layers a subset of the built layer ids', () => {
    for (const id of SNOWMAKING_HIT_LAYERS) expect(SNOWMAKING_BUILT_LAYER_IDS).toContain(id);
    expect(SNOWMAKING_BUILT_LAYER_IDS.length).toBeGreaterThanOrEqual(SNOWMAKING_HIT_LAYERS.length);
  });

  it('renders a route interval, endpoints, pending hydrants, and non-color conflict marks', () => {
    const data = snowmakingDraftToGeoJSON({ points: [], cursor: null, snapPoint: null,
      selectedRoute: [[0, 0], [0, 0.002]], intervalPoints: [[0, 0.0005], [0, 0.0015]],
      startPoint: [0, 0.0005], endPoint: [0, 0.0015], hydrants: [
        { point: [0, 0.0005], conflict: false }, { point: [0, 0.0015], conflict: true },
      ] });
    expect(data.features.map((feature) => feature.properties?.kind)).toEqual([
      'route', 'interval', 'endpoint', 'endpoint', 'hydrant', 'hydrant',
    ]);
    expect(data.features.at(-1)?.properties).toMatchObject({ conflict: true, label: '×' });
    expect(SNOWMAKING_DRAFT_LAYER_IDS).toContain('snowmaking-hydrant-preview-labels');
  });

  it('adds the source and every built layer to the map, idempotently', () => {
    const sources: Record<string, unknown> = {};
    const layers: { id: string; filter?: unknown }[] = [];
    const map = {
      getSource: (id: string) => sources[id],
      addSource: (id: string, src: unknown) => { sources[id] = src; },
      addLayer: (layer: { id: string; filter?: unknown }) => { layers.push(layer); },
      getLayer: (id: string) => layers.find((l) => l.id === id),
      setFilter: (id: string, filter: unknown) => {
        const layer = layers.find((l) => l.id === id);
        if (layer) layer.filter = filter;
      },
    } as unknown as maplibregl.Map;

    addSnowmakingLayers(map);
    expect(sources['snowmaking-network']).toBeTruthy();
    for (const id of SNOWMAKING_BUILT_LAYER_IDS) expect(layers.some((l) => l.id === id)).toBe(true);

    // Idempotent: calling again must not add duplicate layers.
    addSnowmakingLayers(map);
    expect(layers.filter((l) => l.id === 'snowmaking-nodes')).toHaveLength(1);

    setSelectedSnowmakingNode(map, 'node-2');
    const selected = layers.find((l) => l.id === 'snowmaking-node-selected');
    expect(selected?.filter).toEqual(['==', ['get', 'id'], 'node-2']);

    setSelectedSnowmakingNode(map, null);
    expect(selected?.filter).toEqual(['==', ['get', 'id'], '']);

    let lastData: unknown;
    (sources['snowmaking-network'] as { setData: (d: unknown) => void }).setData =
      (d: unknown) => { lastData = d; };
    setSnowmakingData(map, NODES);
    expect((lastData as GeoJSON.FeatureCollection).features).toHaveLength(3);

    // Missing/null map is a safe no-op.
    expect(() => setSnowmakingData(null, NODES)).not.toThrow();
    expect(() => setSelectedSnowmakingNode(null, 'x')).not.toThrow();
  });
});
