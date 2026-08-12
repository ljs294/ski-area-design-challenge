import { describe, expect, it } from 'vitest';
import type maplibregl from 'maplibre-gl';
import {
  SNOWMAKING_BUILT_LAYER_IDS,
  SNOWMAKING_DRAFT_LAYER_IDS,
  SNOWMAKING_HIT_LAYERS,
  SNOWMAKING_HOVER_LAYERS,
  addSnowmakingLayers,
  setSelectedSnowmakingNode,
  setSnowmakingData,
  setSnowgunDraftData,
  setSelectedSnowmakingFeature,
  snowgunDraftToGeoJSON,
  snowmakingNetworkToGeoJSON,
  snowmakingNodesToGeoJSON,
  snowmakingDraftToGeoJSON,
} from './snowmakingLayers';
import type { SavedSnowgun, SavedSnowmakingNode } from '../types/snowmaking';

const NODES: SavedSnowmakingNode[] = [
  { id: 'node-1', name: 'Upper Pond Intake', kind: 'intake', point: [-121.5, 47.2],
    elevM: 1200, source: { kind: 'pond', pondId: 'pond-1' }, createdAt: 'now' },
  { id: 'node-2', name: 'Pump House 1', kind: 'pump', point: [-121.51, 47.21],
    elevM: 1210, createdAt: 'now' },
  { id: 'node-3', name: 'Hydrant 3', kind: 'hydrant', point: [-121.52, 47.22],
    elevM: 1220, createdAt: 'now' },
];
const GUNS: SavedSnowgun[] = [
  { id: 'gun-1', variantId: 'HKD_ImpulseR5_10s', point: [-121.5201, 47.2201],
    elevM: 1221, hydrantId: 'node-3', createdAt: 'now' },
  { id: 'gun-2', variantId: 'HKD_ImpulseR5_20t', point: [-121.54, 47.24],
    elevM: null, hydrantId: null, createdAt: 'now' },
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

  it('uses visible pipe geometry for the clickable hover affordance', () => {
    expect(SNOWMAKING_HOVER_LAYERS).toContain('snowmaking-pipes');
    expect(SNOWMAKING_HOVER_LAYERS).toContain('snowmaking-water-hydrants');
  });

  it('renders installed guns with catalog and connection metadata', () => {
    const data = snowmakingNetworkToGeoJSON(NODES, [], GUNS);
    const guns = data.features.filter((feature) => feature.properties?.entityKind === 'gun');
    const connections = data.features.filter((feature) =>
      feature.properties?.entityKind === 'gun-connection');
    expect(guns).toHaveLength(2);
    expect(connections).toHaveLength(1);
    expect(connections[0].properties).toMatchObject({ gunId: 'gun-1', hydrantId: 'node-3' });
    expect(connections[0].geometry).toEqual({ type: 'LineString',
      coordinates: [NODES[2].point, GUNS[0].point] });
    expect(guns[0].properties).toMatchObject({ id: 'gun-1', variantId: 'HKD_ImpulseR5_10s',
      variantLabel: 'R5 10S', connected: true, hydrantId: 'node-3' });
    expect(guns[1].properties).toMatchObject({ id: 'gun-2', connected: false });
  });

  it('draws draft hookups and a non-color disconnected warning', () => {
    const data = snowgunDraftToGeoJSON({ guns: [
      { point: [0, 0], hydrantPoint: [0, 0.0001], connected: true },
      { point: [1, 1], hydrantPoint: null, connected: false },
    ], candidate: { point: [2, 2], hydrantPoint: null, connected: false } });
    expect(data.features.map((feature) => feature.properties?.kind)).toEqual([
      'gun-hose', 'gun', 'gun', 'gun',
    ]);
    expect(SNOWMAKING_DRAFT_LAYER_IDS).toContain('snowmaking-gun-draft-warnings');
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

  it('renders the chosen inline pump flow direction as an arrow path', () => {
    const data = snowmakingDraftToGeoJSON({ points: [], cursor: null, snapPoint: [0, 0.001],
      pumpDirection: [[0, 0], [0, 0.001], [0, 0.002]] });
    expect(data.features.map((feature) => feature.properties?.kind)).toEqual([
      'snap', 'pump-direction',
    ]);
    expect(SNOWMAKING_DRAFT_LAYER_IDS).toContain('snowmaking-pump-direction-preview');
  });

  it('adds the source and every built layer to the map, idempotently', () => {
    const sources: Record<string, unknown> = {};
    const layers: { id: string; filter?: unknown; paint?: Record<string, unknown>;
      layout?: Record<string, unknown> }[] = [];
    const map = {
      getSource: (id: string) => sources[id],
      addSource: (id: string, src: unknown) => { sources[id] = src; },
      addLayer: (layer: { id: string; filter?: unknown; paint?: Record<string, unknown>;
        layout?: Record<string, unknown> }) => { layers.push(layer); },
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
    expect(layers.find((l) => l.id === 'snowmaking-water-hydrants')?.layout?.['text-field'])
      .toBe('×');
    expect(layers.find((l) => l.id === 'snowmaking-air-hydrants')?.layout?.['text-field'])
      .toBe('O');
    expect(layers.find((l) => l.id === 'snowmaking-guns')?.paint?.['circle-color'])
      .toBe('#000000');
    expect(layers.find((l) => l.id === 'snowmaking-gun-connections')?.paint?.['line-width'])
      .toBe(1);
    expect(layers.find((l) => l.id === 'snowmaking-pipe-casing')?.paint?.['line-color'])
      .not.toBe('#ffffff');

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
    setSelectedSnowmakingFeature(map, { kind: 'gun', id: 'gun-1' });
    expect(layers.find((layer) => layer.id === 'snowmaking-gun-selected')?.filter)
      .toEqual(['all', ['==', ['get', 'entityKind'], 'gun'], ['==', ['get', 'id'], 'gun-1']]);
    expect(() => setSnowgunDraftData(null, { guns: [], candidate: null })).not.toThrow();
  });
});
